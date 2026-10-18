using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Purchasing.Documents;

public sealed class FxRevaluation : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string RevaluationNo { get; init; }
    public DateOnly RevaluationDate { get; init; }
    public DateOnly ReversalDate { get; init; }
    public required string Rates { get; init; }
    public decimal TotalDifference { get; init; }
    public Guid? JournalEntryId { get; set; }
}

internal sealed class FxRevaluationConfiguration : IEntityTypeConfiguration<FxRevaluation>
{
    public void Configure(EntityTypeBuilder<FxRevaluation> b) => b.ToTable("fx_revaluation", "pur");
}

public sealed record ClosingRate(string Currency, decimal Rate);

public sealed record FxRevaluationRequest(DateOnly Date, IReadOnlyList<ClosingRate> Rates);

/// <summary>One open invoice: booked QAR, QAR at the closing rate, and the difference (positive = more owed, a loss).</summary>
public sealed record FxRevaluationLine(Guid InvoiceId, string InvoiceNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, string Currency,
    decimal OpenAmount, decimal BookedBase, decimal ClosingRate, decimal RevaluedBase, decimal Difference);

public sealed record FxRevaluationResult(Guid? Id, string? RevaluationNo, DateOnly Date, DateOnly ReversalDate, IReadOnlyList<FxRevaluationLine> Lines,
    decimal TotalDifference, Guid? JournalEntryId);

/// <summary>
/// Restates open foreign-currency supplier balances at closing rates for the month-end balance sheet. The unrealised difference
/// goes to exchange differences and the entry is reversed on the next day, so payments later clear the invoices at their booked QAR
/// and recognise the realised difference as usual.
/// </summary>
internal sealed class FxRevaluationService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting)
{
    public const string SourceType = "pur_fx_revaluation";

    public async Task<FxRevaluationResult> PreviewAsync(FxRevaluationRequest r, CancellationToken ct)
    {
        var lines = await LinesAsync(r, ct);
        return new FxRevaluationResult(null, null, r.Date, r.Date.AddDays(1), lines, lines.Sum(l => l.Difference), null);
    }

    public Task<FxRevaluationResult> PostAsync(FxRevaluationRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var lines = await LinesAsync(r, token);
            if (lines.All(l => l.Difference == 0))
                throw new BusinessRuleException("pur.fx_nothing", "There is no difference to post at these rates.", "لا توجد فروقات للترحيل بهذه الأسعار.");
            var companyId = tenant.RequireCompanyId();
            var revaluation = new FxRevaluation
            {
                CompanyId = companyId, RevaluationNo = await numbering.NextAsync(SourceType, "FXR", r.Date, ct: token), RevaluationDate = r.Date,
                ReversalDate = r.Date.AddDays(1), Rates = string.Join(", ", r.Rates.Select(x => $"{x.Currency.ToUpperInvariant()} {x.Rate:0.######}")),
                TotalDifference = lines.Sum(l => l.Difference)
            };
            db.Add(revaluation);
            var postingLines = new List<PostingLine>();
            foreach (var bySupplier in lines.Where(l => l.Difference != 0).GroupBy(l => l.SupplierId))
            {
                var diff = bySupplier.Sum(l => l.Difference);
                if (diff == 0) continue;
                postingLines.Add(new PostingLine
                {
                    AccountRole = AccountRoles.AccountsPayable, Side = diff > 0 ? PostingSide.Credit : PostingSide.Debit, Amount = Math.Abs(diff),
                    PartyType = PartyType.Supplier, PartyId = bySupplier.Key, Description = "Unrealised exchange difference"
                });
                postingLines.Add(new PostingLine
                {
                    AccountRole = AccountRoles.ExchangeDifference, Side = diff > 0 ? PostingSide.Debit : PostingSide.Credit, Amount = Math.Abs(diff),
                    Description = "Unrealised exchange difference"
                });
            }
            revaluation.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = revaluation.Id, SourceNo = revaluation.RevaluationNo, PostingDate = r.Date,
                MemoEn = $"Revaluation of foreign-currency payables ({revaluation.Rates})", MemoAr = $"إعادة تقييم الذمم الدائنة بالعملات الأجنبية ({revaluation.Rates})",
                Lines = postingLines
            }, token)).JournalEntryId;
            await posting.ReverseAsync(SourceType, revaluation.Id, revaluation.ReversalDate, "Automatic reversal of month-end revaluation", token);
            await db.SaveChangesAsync(token);
            return new FxRevaluationResult(revaluation.Id, revaluation.RevaluationNo, r.Date, revaluation.ReversalDate, lines, revaluation.TotalDifference,
                revaluation.JournalEntryId);
        }, ct);

    private async Task<List<FxRevaluationLine>> LinesAsync(FxRevaluationRequest r, CancellationToken ct)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in r.Rates ?? [])
        {
            if (x.Rate <= 0)
                throw new BusinessRuleException("pur.fx_rate", $"The rate for {x.Currency} must be positive.", $"يجب أن يكون سعر {x.Currency} موجباً.");
            rates[PurchasingRules.CurrencyCode(x.Currency)] = x.Rate;
        }
        var companyId = tenant.RequireCompanyId();
        var invoices = await (from i in db.Set<SupplierInvoice>().AsNoTracking()
                              join s in db.Set<Supplier>() on i.SupplierId equals s.Id
                              where i.CompanyId == companyId && i.Currency != Money.BaseCurrency && i.InvoiceDate <= r.Date
                                    && (i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid)
                              select new { i, s.NameEn, s.NameAr }).ToListAsync(ct);
        var missing = invoices.Select(x => x.i.Currency).Distinct().Where(c => !rates.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new BusinessRuleException("pur.fx_rate_missing", $"Enter a closing rate for {string.Join(", ", missing)}.",
                $"أدخل سعر الإقفال لـ {string.Join("، ", missing)}.");
        return invoices.Where(x => x.i.OpenAmount > 0).OrderBy(x => x.i.Currency).ThenBy(x => x.i.InvoiceNo)
            .Select(x =>
            {
                var rate = rates[x.i.Currency];
                var revalued = Money.Round(x.i.OpenAmount * rate);
                return new FxRevaluationLine(x.i.Id, x.i.InvoiceNo, x.i.SupplierId, x.NameEn, x.NameAr, x.i.Currency, x.i.OpenAmount, x.i.OpenBase, rate,
                    revalued, revalued - x.i.OpenBase);
            }).ToList();
    }
}

internal static class FxRevaluationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing/fx-revaluations").WithTags("Purchasing").RequireAuthorization();
        g.MapGet("", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await db.Set<FxRevaluation>().AsNoTracking().Where(x => x.CompanyId == t.RequireCompanyId()).OrderByDescending(x => x.RevaluationDate)
                .Select(x => new { x.Id, x.RevaluationNo, x.RevaluationDate, x.ReversalDate, x.Rates, x.TotalDifference, x.JournalEntryId }).ToListAsync(ct))
            .RequirePermission(PurchasingPermissions.ReportsRead);
        g.MapPost("/preview", (FxRevaluationRequest r, FxRevaluationService s, CancellationToken ct) => s.PreviewAsync(r, ct))
            .RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("", (FxRevaluationRequest r, FxRevaluationService s, CancellationToken ct) => s.PostAsync(r, ct))
            .RequirePermission(PurchasingPermissions.InvoicesPost);
    }
}

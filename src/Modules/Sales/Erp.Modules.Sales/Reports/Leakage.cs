using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Sales.Pos;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Reports;

/// <summary>
/// One sign that money may be leaking. <see cref="Kind"/>: cash_shortage, refunds, discounts, below_cost, invoice_discounts.
/// <see cref="Severity"/>: "warn" (worth a look) or "bad" (act now). These are signals for the owner to check, not proof of wrongdoing.
/// </summary>
public sealed record LeakageFinding(string Kind, string Severity, string? Person, decimal Amount, int Count, decimal? Percent, string En, string Ar);

public sealed record LeakageReport(DateOnly From, DateOnly To, IReadOnlyList<LeakageFinding> Findings, decimal AmountAtRisk);

/// <summary>Till and sales patterns that often hide losses: cash shortages, refunds and discounts by cashier, selling below cost.</summary>
internal sealed class LeakageService(ErpDbContext db, ITenantContext tenant, IUserDirectory users, IClock clock)
{
    // What counts as worth a look. Deliberately simple and explained in each finding.
    public const decimal ShortageBad = 50m;
    public const decimal RefundWarnPercent = 5m, RefundBadPercent = 10m, RefundMinimum = 100m;
    public const decimal DiscountWarnPercent = 5m, DiscountBadPercent = 10m, DiscountMinimum = 50m;
    public const decimal InvoiceDiscountPercent = 15m;
    public const decimal BelowCostBad = 200m;

    public async Task<LeakageReport> BuildAsync(int days, CancellationToken ct)
    {
        var periodEnd = clock.Today;
        var periodStart = periodEnd.AddDays(-Math.Clamp(days, 1, 366) + 1);
        var companyId = tenant.RequireCompanyId();
        var findings = new List<LeakageFinding>();

        var registers = await db.Set<PosRegister>().AsNoTracking().Where(r => r.CompanyId == companyId).Select(r => r.Id).ToListAsync(ct);
        var shifts = await db.Set<PosShift>().AsNoTracking()
            .Where(s => registers.Contains(s.RegisterId) && s.BusinessDate >= periodStart && s.BusinessDate <= periodEnd).ToListAsync(ct);
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var cashierOf = shifts.ToDictionary(s => s.Id, s => s.CashierUserId);
        var sales = await db.Set<PosSale>().AsNoTracking().Where(s => shiftIds.Contains(s.ShiftId))
            .Select(s => new { s.Id, s.ShiftId, s.Total, s.DiscountTotal, s.RefundOfId }).ToListAsync(ct);
        var names = await users.NamesAsync(shifts.Select(s => s.CashierUserId), ct);
        string Name(Guid id) => names.GetValueOrDefault(id, "?");

        // Cash counted short at the end of shifts.
        foreach (var g in shifts.Where(s => s.Difference < 0).GroupBy(s => s.CashierUserId))
        {
            var amount = -g.Sum(s => s.Difference!.Value);
            var count = g.Count();
            findings.Add(new("cash_shortage", amount >= ShortageBad || count >= 3 ? "bad" : "warn", Name(g.Key), amount, count, null,
                $"{count} shift(s) closed with less cash than expected, {amount:N2} QAR short in total.",
                $"{count} وردية أُغلقت بنقد أقل من المتوقع، بعجز إجمالي {amount:N2} ريال."));
        }

        // Refunds and discounts per cashier, against what they sold.
        foreach (var g in sales.GroupBy(s => cashierOf[s.ShiftId]))
        {
            var sold = g.Where(s => s.RefundOfId == null).Sum(s => s.Total);
            var refunded = -g.Where(s => s.RefundOfId != null).Sum(s => s.Total);
            var refunds = g.Count(s => s.RefundOfId != null);
            if (sold > 0 && refunded >= RefundMinimum)
            {
                var pct = Math.Round(refunded / sold * 100, 1);
                if (pct >= RefundWarnPercent)
                    findings.Add(new("refunds", pct >= RefundBadPercent ? "bad" : "warn", Name(g.Key), refunded, refunds, pct,
                        $"Refunds of {refunded:N2} QAR ({refunds}) are {pct}% of this cashier's sales.",
                        $"المبالغ المستردة {refunded:N2} ريال ({refunds}) تساوي {pct}% من مبيعات هذا الكاشير."));
            }
            var discount = g.Where(s => s.RefundOfId == null).Sum(s => s.DiscountTotal);
            if (sold + discount > 0 && discount >= DiscountMinimum)
            {
                var pct = Math.Round(discount / (sold + discount) * 100, 1);
                if (pct >= DiscountWarnPercent)
                    findings.Add(new("discounts", pct >= DiscountBadPercent ? "bad" : "warn", Name(g.Key), discount, g.Count(s => s.DiscountTotal > 0), pct,
                        $"Discounts of {discount:N2} QAR, {pct}% of the full price of this cashier's sales.",
                        $"خصومات بقيمة {discount:N2} ريال، أي {pct}% من السعر الكامل لمبيعات هذا الكاشير."));
            }
        }

        // Sold for less than it cost (till and invoices).
        var saleIds = sales.Where(s => s.RefundOfId == null).Select(s => s.Id).ToList();
        var tillLoss = await db.Set<PosSaleLine>().AsNoTracking().Where(l => saleIds.Contains(l.PosSaleId) && l.Cost > l.Amount && l.Quantity > 0)
            .Select(l => new { Loss = l.Cost - l.Amount }).ToListAsync(ct);
        var invoiceLoss = await (from l in db.Set<SalesInvoiceLine>().AsNoTracking()
                                 join i in db.Set<SalesInvoice>() on l.SalesInvoiceId equals i.Id
                                 where i.CompanyId == companyId && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd && i.Status != InvoiceStatus.Draft
                                       && i.Status != InvoiceStatus.Reversed && l.Cost > l.Amount * i.ExchangeRate && l.Quantity > 0
                                       && !db.Set<PosSale>().Any(p => p.SalesInvoiceId == i.Id)
                                 select new { Loss = l.Cost - l.Amount * i.ExchangeRate }).ToListAsync(ct);
        var loss = tillLoss.Sum(x => x.Loss) + invoiceLoss.Sum(x => x.Loss);
        var lossCount = tillLoss.Count + invoiceLoss.Count;
        if (lossCount > 0)
            findings.Add(new("below_cost", loss >= BelowCostBad ? "bad" : "warn", null, Math.Round(loss, 2), lossCount, null,
                $"{lossCount} sale line(s) were sold below cost, {loss:N2} QAR lost. Check prices and discounts.",
                $"{lossCount} بند بيع بسعر أقل من التكلفة، بخسارة {loss:N2} ريال. راجع الأسعار والخصومات."));

        // Large discounts on invoices.
        var bigDiscounts = await db.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd && i.Status != InvoiceStatus.Draft
                        && i.Status != InvoiceStatus.Reversed && i.DiscountTotal > 0
                        && i.DiscountTotal * 100 >= (i.Total + i.DiscountTotal) * InvoiceDiscountPercent)
            .Select(i => new { i.DiscountTotal, i.ExchangeRate }).ToListAsync(ct);
        if (bigDiscounts.Count > 0)
        {
            var amount = bigDiscounts.Sum(i => i.DiscountTotal * i.ExchangeRate);
            findings.Add(new("invoice_discounts", "warn", null, Math.Round(amount, 2), bigDiscounts.Count, InvoiceDiscountPercent,
                $"{bigDiscounts.Count} invoice(s) with a discount of {InvoiceDiscountPercent}% or more, {amount:N2} QAR in total.",
                $"{bigDiscounts.Count} فاتورة بخصم {InvoiceDiscountPercent}% أو أكثر، بإجمالي {amount:N2} ريال."));
        }

        var ordered = findings.OrderByDescending(f => f.Severity == "bad").ThenByDescending(f => f.Amount).ToList();
        return new LeakageReport(periodStart, periodEnd, ordered, ordered.Where(f => f.Kind is "cash_shortage" or "below_cost").Sum(f => f.Amount));
    }
}

/// <summary>"Possible money leaks" on the dashboard when there is something to check.</summary>
internal sealed class LeakageDashboard(LeakageService leakage) : IDashboardProvider
{
    public string Permission => SalesPermissions.ReportsRead;
    public string? Module => ModuleCodes.Sales;

    public async Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct)
    {
        var r = await leakage.BuildAsync(30, ct);
        if (r.Findings.Count == 0) return [];
        var bad = r.Findings.Any(f => f.Severity == "bad");
        return
        [
            new("today", "sal.leaks", "Possible money leaks (30 days)", "تسربات مالية محتملة (30 يوماً)", r.Findings.Count, "count", "/sales/leaks",
                $"{r.AmountAtRisk:N2} QAR short or below cost", $"{r.AmountAtRisk:N2} ريال عجز أو بيع تحت التكلفة", bad ? "bad" : "warn")
        ];
    }
}

/// <summary>The same signals for the AI assistant (cashier names are hidden from shared AI providers).</summary>
internal sealed class LeakageAiTool(LeakageService leakage) : IAiTool
{
    public string Name => "money_leaks";
    public string Description =>
        "Signs that money may be leaking in the last days: cash shortages at the till, many refunds or discounts by a cashier, sales below cost, big invoice discounts. These are signals to check, not proof.";
    public string Permission => SalesPermissions.ReportsRead;
    public string? Module => ModuleCodes.Sales;
    public string ParametersSchema => """{"type":"object","properties":{"days":{"type":"integer","description":"how many days back, default 30"}}}""";

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var r = await leakage.BuildAsync(AiArgs.Int(args, "days", 30, 1, 366), ct);
        return new
        {
            r.From, r.To, amount_short_or_below_cost = r.AmountAtRisk,
            findings = r.Findings.Select(f => new { kind = f.Kind, severity = f.Severity, employee = f.Person, amount = f.Amount, count = f.Count, percent = f.Percent, detail = f.En })
        };
    }
}

internal static class LeakageEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/sales/reports/leakage", (LeakageService s, CancellationToken ct, int days = 30) => s.BuildAsync(days, ct))
            .WithTags("Sales").RequireAuthorization().RequirePermission(SalesPermissions.ReportsRead);
}

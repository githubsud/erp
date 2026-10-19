using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Finance.Assets;

public enum AssetStatus { Draft, Active, FullyDepreciated, Disposed }

public sealed class AssetCategory : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public int UsefulLifeMonths { get; set; }
    public Guid AssetAccountId { get; set; }
    public Guid AccumulatedAccountId { get; set; }
    public Guid ExpenseAccountId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class FixedAsset : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string AssetNo { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public Guid CategoryId { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Draft;
    public DateOnly AcquisitionDate { get; set; }
    public DateOnly DepreciationStart { get; set; }
    public decimal Cost { get; set; }
    public decimal SalvageValue { get; set; }
    public int UsefulLifeMonths { get; set; }

    /// <summary>Total depreciation to date, including <see cref="OpeningDepreciation"/> brought over from a previous system.</summary>
    public decimal AccumulatedDepreciation { get; set; }
    public decimal OpeningDepreciation { get; set; }
    public Guid? CostCenterId { get; set; }
    public string? Location { get; set; }
    public string? SerialNo { get; set; }
    public Guid? CapitalizationEntryId { get; set; }
    public DateOnly? DisposalDate { get; set; }
    public decimal? DisposalProceeds { get; set; }
    public Guid? DisposalEntryId { get; set; }
    public string? Notes { get; set; }

    public decimal NetBookValue => Cost - AccumulatedDepreciation;
    public decimal Depreciable => Cost - SalvageValue;
}

public sealed class DepreciationRun : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string RunNo { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public DateOnly PostingDate { get; init; }
    public decimal Total { get; set; }
    public VoucherStatus Status { get; set; } = VoucherStatus.Posted;
    public Guid? JournalEntryId { get; set; }
    public List<DepreciationLine> Lines { get; init; } = [];
}

public sealed class DepreciationLine : TenantEntity
{
    public Guid DepreciationRunId { get; init; }
    public Guid FixedAssetId { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class AssetCategoryConfiguration : IEntityTypeConfiguration<AssetCategory>
{
    public void Configure(EntityTypeBuilder<AssetCategory> b) => b.ToTable("asset_category", "fin");
}

internal sealed class FixedAssetConfiguration : IEntityTypeConfiguration<FixedAsset>
{
    public void Configure(EntityTypeBuilder<FixedAsset> b) => b.ToTable("fixed_asset", "fin");
}

internal sealed class DepreciationRunConfiguration : IEntityTypeConfiguration<DepreciationRun>
{
    public void Configure(EntityTypeBuilder<DepreciationRun> b)
    {
        b.ToTable("depreciation_run", "fin");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DepreciationRunId);
    }
}

internal sealed class DepreciationLineConfiguration : IEntityTypeConfiguration<DepreciationLine>
{
    public void Configure(EntityTypeBuilder<DepreciationLine> b) => b.ToTable("depreciation_line", "fin");
}

public sealed record AssetCategoryRequest(string Code, string? NameEn, string? NameAr, int UsefulLifeMonths, Guid AssetAccountId, Guid AccumulatedAccountId,
    Guid ExpenseAccountId, bool IsActive = true);

public sealed record AssetCategoryDto(Guid Id, string Code, string NameEn, string NameAr, int UsefulLifeMonths, Guid AssetAccountId, Guid AccumulatedAccountId,
    Guid ExpenseAccountId, bool IsActive);

/// <summary><see cref="OpeningDepreciation"/>: depreciation already charged before go-live (existing assets brought over).</summary>
public sealed record FixedAssetRequest(string? NameEn, string? NameAr, Guid CategoryId, DateOnly AcquisitionDate, DateOnly? DepreciationStart, decimal Cost,
    decimal SalvageValue, int? UsefulLifeMonths, Guid? CostCenterId, string? Location, string? SerialNo, string? Notes, decimal OpeningDepreciation = 0);

/// <summary>The account credited when the asset is capitalised: the bank or cash paid, a payable clearing account, or opening balance equity.</summary>
public sealed record CapitalizeRequest(DateOnly Date, Guid OffsetAccountId);

public sealed record DepreciationRequest(int Year, int Month);

public sealed record DisposalRequest(DateOnly Date, decimal Proceeds, Guid? ProceedsAccountId, string? Reason);

public sealed record FixedAssetDto(Guid Id, string AssetNo, string NameEn, string NameAr, Guid CategoryId, string CategoryCode, string Status,
    DateOnly AcquisitionDate, DateOnly DepreciationStart, decimal Cost, decimal SalvageValue, int UsefulLifeMonths, decimal MonthlyDepreciation,
    decimal AccumulatedDepreciation, decimal NetBookValue, Guid? CostCenterId, string? Location, string? SerialNo, DateOnly? DisposalDate,
    decimal? DisposalProceeds, string? Notes);

public sealed record DepreciationRunDto(Guid Id, string RunNo, int Year, int Month, DateOnly PostingDate, decimal Total, string Status, Guid? JournalEntryId,
    int AssetCount);

public sealed record AssetRegister(DateOnly AsOf, IReadOnlyList<FixedAssetDto> Assets, decimal Cost, decimal Accumulated, decimal NetBookValue,
    decimal LedgerCost, decimal LedgerAccumulated);

/// <summary>
/// Fixed assets with straight-line depreciation. Capitalising debits the category's asset account; each monthly run depreciates
/// every active asset by (cost − salvage) ÷ useful life (the last month takes what is left) into one journal entry; disposal clears
/// cost and accumulated depreciation and books the gain or loss.
/// </summary>
internal sealed class FixedAssetService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting)
{
    public const string SourceType = "fixed_asset";
    public const string RunSourceType = "depreciation_run";
    public const string DisposalSourceType = "asset_disposal";

    public async Task<AssetCategory> SaveCategoryAsync(Guid? id, AssetCategoryRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        if (r.UsefulLifeMonths is < 1 or > 1200)
            throw new BusinessRuleException("fin.useful_life", "Useful life must be between 1 and 1,200 months.", "يجب أن يكون العمر الإنتاجي بين 1 و1200 شهر.");
        foreach (var accountId in new[] { r.AssetAccountId, r.AccumulatedAccountId, r.ExpenseAccountId })
            if (!await db.Set<Account>().AnyAsync(a => a.Id == accountId && a.IsPostable && a.IsActive && a.CompanyId == tenant.RequireCompanyId(), ct))
                throw new NotFoundException("Account", accountId);
        var category = id is null
            ? db.Add(new AssetCategory { CompanyId = tenant.RequireCompanyId(), Code = Guard.Required(r.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar }).Entity
            : await db.Set<AssetCategory>().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == tenant.RequireCompanyId(), ct) ?? throw new NotFoundException("Asset category", id);
        category.NameEn = en;
        category.NameAr = ar;
        category.UsefulLifeMonths = r.UsefulLifeMonths;
        category.AssetAccountId = r.AssetAccountId;
        category.AccumulatedAccountId = r.AccumulatedAccountId;
        category.ExpenseAccountId = r.ExpenseAccountId;
        category.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return category;
    }

    public Task<FixedAsset> SaveAsync(Guid? id, FixedAssetRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
            var category = await db.Set<AssetCategory>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CategoryId && c.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Asset category", r.CategoryId);
            if (r.Cost <= 0 || r.SalvageValue < 0 || r.SalvageValue >= r.Cost)
                throw new BusinessRuleException("fin.asset_values", "Cost must be positive and the salvage value below it.", "يجب أن تكون التكلفة موجبة والقيمة التخريدية أقل منها.");
            if (r.OpeningDepreciation < 0 || r.OpeningDepreciation > r.Cost - r.SalvageValue)
                throw new BusinessRuleException("fin.asset_opening", "Opening depreciation cannot exceed the depreciable amount.", "لا يمكن أن يتجاوز الإهلاك الافتتاحي القيمة القابلة للإهلاك.");
            FixedAsset asset;
            if (id is null)
            {
                asset = new FixedAsset
                {
                    CompanyId = tenant.RequireCompanyId(), AssetNo = await numbering.NextAsync(SourceType, "FA", r.AcquisitionDate, ct: token), NameEn = en, NameAr = ar
                };
                db.Add(asset);
            }
            else
            {
                asset = await FindAsync(id.Value, token);
                if (asset.Status != AssetStatus.Draft)
                    throw new ConflictException("fin.asset_not_draft", "Only assets not yet capitalised can be edited.", "يمكن تعديل الأصول غير المرسملة فقط.");
            }
            asset.NameEn = en;
            asset.NameAr = ar;
            asset.CategoryId = category.Id;
            asset.AcquisitionDate = r.AcquisitionDate;
            asset.DepreciationStart = r.DepreciationStart ?? new DateOnly(r.AcquisitionDate.Year, r.AcquisitionDate.Month, 1);
            asset.Cost = Money.Round(r.Cost);
            asset.SalvageValue = Money.Round(r.SalvageValue);
            asset.UsefulLifeMonths = r.UsefulLifeMonths ?? category.UsefulLifeMonths;
            asset.OpeningDepreciation = Money.Round(r.OpeningDepreciation);
            asset.AccumulatedDepreciation = asset.OpeningDepreciation;
            asset.CostCenterId = r.CostCenterId;
            asset.Location = Clean(r.Location);
            asset.SerialNo = Clean(r.SerialNo);
            asset.Notes = Clean(r.Notes);
            await db.SaveChangesAsync(token);
            return asset;
        }, ct);

    /// <summary>Debits the asset account at cost (and credits accumulated depreciation with any opening depreciation).</summary>
    public Task<FixedAsset> CapitalizeAsync(Guid id, CapitalizeRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var asset = await FindAsync(id, token);
            if (asset.Status != AssetStatus.Draft)
                throw new ConflictException("fin.asset_not_draft", "This asset is already capitalised.", "تمت رسملة هذا الأصل مسبقاً.");
            var category = await db.Set<AssetCategory>().AsNoTracking().SingleAsync(c => c.Id == asset.CategoryId, token);
            var offset = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == r.OffsetAccountId && a.IsPostable && a.IsActive, token)
                         ?? throw new NotFoundException("Account", r.OffsetAccountId);
            List<PostingLine> lines =
            [
                new() { AccountId = category.AssetAccountId, Side = PostingSide.Debit, Amount = asset.Cost, CostCenterId = asset.CostCenterId },
                new() { AccountId = offset.Id, Side = PostingSide.Credit, Amount = asset.Cost - asset.OpeningDepreciation }
            ];
            if (asset.OpeningDepreciation > 0)
                lines.Add(new PostingLine { AccountId = category.AccumulatedAccountId, Side = PostingSide.Credit, Amount = asset.OpeningDepreciation });
            asset.CapitalizationEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = asset.Id, SourceNo = asset.AssetNo, PostingDate = r.Date,
                MemoEn = $"Fixed asset {asset.AssetNo} {asset.NameEn}", MemoAr = $"أصل ثابت {asset.AssetNo} {asset.NameAr}", Lines = lines, AllowControlAccounts = false
            }, token)).JournalEntryId;
            asset.Status = asset.AccumulatedDepreciation >= asset.Depreciable ? AssetStatus.FullyDepreciated : AssetStatus.Active;
            await db.SaveChangesAsync(token);
            return asset;
        }, ct);

    /// <summary>Depreciates every active asset for the month, once per month; one entry, a line per asset kept for the register.</summary>
    public Task<DepreciationRun> RunAsync(DepreciationRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Month is < 1 or > 12) throw new BusinessRuleException("fin.month", "Month must be 1 to 12.", "يجب أن يكون الشهر من 1 إلى 12.");
            var companyId = tenant.RequireCompanyId();
            if (await db.Set<DepreciationRun>().AnyAsync(x => x.CompanyId == companyId && x.Year == r.Year && x.Month == r.Month && x.Status == VoucherStatus.Posted, token))
                throw new ConflictException("fin.depreciation_done", $"Depreciation for {r.Month:D2}/{r.Year} is already posted.", $"تم ترحيل إهلاك {r.Month:D2}/{r.Year} مسبقاً.");
            var monthStart = new DateOnly(r.Year, r.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var assets = await db.Set<FixedAsset>()
                .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active && a.DepreciationStart <= monthEnd).ToListAsync(token);
            var categories = await db.Set<AssetCategory>().AsNoTracking().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Id, token);
            var run = new DepreciationRun
            {
                CompanyId = companyId, RunNo = await numbering.NextAsync(RunSourceType, "DEP", monthEnd, ct: token), Year = r.Year, Month = r.Month, PostingDate = monthEnd
            };
            foreach (var asset in assets.OrderBy(a => a.AssetNo))
            {
                var amount = MonthlyCharge(asset);
                if (amount <= 0) continue;
                asset.AccumulatedDepreciation += amount;
                if (asset.AccumulatedDepreciation >= asset.Depreciable) asset.Status = AssetStatus.FullyDepreciated;
                run.Lines.Add(new DepreciationLine { DepreciationRunId = run.Id, FixedAssetId = asset.Id, Amount = amount });
            }
            if (run.Lines.Count == 0)
                throw new BusinessRuleException("fin.depreciation_nothing", "No asset has depreciation for this month.", "لا يوجد إهلاك لأي أصل في هذا الشهر.");
            run.Total = run.Lines.Sum(l => l.Amount);
            db.Add(run);
            var byAsset = assets.ToDictionary(a => a.Id);
            var lines = new List<PostingLine>();
            foreach (var g in run.Lines.GroupBy(l => (Category: byAsset[l.FixedAssetId].CategoryId, Center: byAsset[l.FixedAssetId].CostCenterId)))
            {
                var category = categories[g.Key.Category];
                var total = g.Sum(l => l.Amount);
                lines.Add(new PostingLine { AccountId = category.ExpenseAccountId, Side = PostingSide.Debit, Amount = total, CostCenterId = g.Key.Center });
                lines.Add(new PostingLine { AccountId = category.AccumulatedAccountId, Side = PostingSide.Credit, Amount = total });
            }
            run.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = RunSourceType, SourceId = run.Id, SourceNo = run.RunNo, PostingDate = monthEnd,
                MemoEn = $"Depreciation {r.Month:D2}/{r.Year}", MemoAr = $"إهلاك {r.Month:D2}/{r.Year}", Lines = lines, AllowControlAccounts = false
            }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return run;
        }, ct);

    public Task<DepreciationRun> ReverseRunAsync(Guid id, string reason, DateOnly date, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await db.Set<DepreciationRun>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), token)
                      ?? throw new NotFoundException("Depreciation run", id);
            if (run.Status != VoucherStatus.Posted) throw new ConflictException("fin.not_posted", "This run is already reversed.", "تم عكس هذا الإهلاك مسبقاً.");
            var later = await db.Set<DepreciationRun>().AnyAsync(x => x.CompanyId == run.CompanyId && x.Status == VoucherStatus.Posted
                                                                   && (x.Year > run.Year || (x.Year == run.Year && x.Month > run.Month)), token);
            if (later) throw new BusinessRuleException("fin.depreciation_order", "Reverse the later months first.", "اعكس الأشهر اللاحقة أولاً.");
            var ids = run.Lines.Select(l => l.FixedAssetId).ToList();
            var assets = await db.Set<FixedAsset>().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, token);
            if (assets.Values.Any(a => a.Status == AssetStatus.Disposed))
                throw new BusinessRuleException("fin.depreciation_disposed", "An asset in this run was disposed of since.", "تم استبعاد أصل من هذا الإهلاك بعد ترحيله.");
            foreach (var l in run.Lines)
            {
                var asset = assets[l.FixedAssetId];
                asset.AccumulatedDepreciation -= l.Amount;
                asset.Status = AssetStatus.Active;
            }
            await posting.ReverseAsync(RunSourceType, run.Id, date, Guard.Required(reason, "reason"), token);
            run.Status = VoucherStatus.Reversed;
            await db.SaveChangesAsync(token);
            return run;
        }, ct);

    /// <summary>Sale or write-off: removes cost and accumulated depreciation; proceeds less net book value is the gain (or loss).</summary>
    public Task<FixedAsset> DisposeAsync(Guid id, DisposalRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var asset = await FindAsync(id, token);
            if (asset.Status is not (AssetStatus.Active or AssetStatus.FullyDepreciated))
                throw new ConflictException("fin.asset_not_active", "Only capitalised assets can be disposed of.", "يمكن استبعاد الأصول المرسملة فقط.");
            if (r.Proceeds < 0) throw new BusinessRuleException("fin.asset_proceeds", "Proceeds cannot be negative.", "لا يمكن أن تكون حصيلة البيع سالبة.");
            if (r.Proceeds > 0 && r.ProceedsAccountId is null)
                throw new BusinessRuleException("fin.asset_proceeds_account", "Choose the account the proceeds were received in.", "اختر الحساب الذي استلمت فيه الحصيلة.");
            var category = await db.Set<AssetCategory>().AsNoTracking().SingleAsync(c => c.Id == asset.CategoryId, token);
            var gainAccount = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.CompanyId == asset.CompanyId && a.Code == "4230", token)
                              ?? throw new BusinessRuleException("fin.disposal_account", "Account 4230 (gain or loss on disposal) is missing.", "الحساب 4230 (أرباح أو خسائر بيع الأصول) غير موجود.");
            var proceeds = Money.Round(r.Proceeds);
            var gain = proceeds - asset.NetBookValue;
            List<PostingLine> lines =
            [
                new() { AccountId = category.AssetAccountId, Side = PostingSide.Credit, Amount = asset.Cost }
            ];
            if (asset.AccumulatedDepreciation > 0)
                lines.Add(new PostingLine { AccountId = category.AccumulatedAccountId, Side = PostingSide.Debit, Amount = asset.AccumulatedDepreciation });
            if (proceeds > 0) lines.Add(new PostingLine { AccountId = r.ProceedsAccountId, Side = PostingSide.Debit, Amount = proceeds });
            if (gain != 0)
                lines.Add(new PostingLine { AccountId = gainAccount.Id, Side = gain > 0 ? PostingSide.Credit : PostingSide.Debit, Amount = Math.Abs(gain) });
            asset.DisposalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = DisposalSourceType, SourceId = asset.Id, SourceNo = asset.AssetNo, PostingDate = r.Date,
                MemoEn = $"Disposal of {asset.AssetNo} {asset.NameEn}{(r.Reason is null ? "" : " — " + r.Reason)}", MemoAr = $"استبعاد {asset.AssetNo} {asset.NameAr}",
                Lines = lines, AllowControlAccounts = false
            }, token)).JournalEntryId;
            asset.Status = AssetStatus.Disposed;
            asset.DisposalDate = r.Date;
            asset.DisposalProceeds = proceeds;
            await db.SaveChangesAsync(token);
            return asset;
        }, ct);

    /// <summary>Straight line: (cost − salvage) ÷ life per month; the last month takes the remainder so the asset ends at salvage value.</summary>
    internal static decimal MonthlyCharge(FixedAsset a)
    {
        var remaining = a.Depreciable - a.AccumulatedDepreciation;
        if (remaining <= 0) return 0;
        return Math.Min(remaining, Money.Round(a.Depreciable / a.UsefulLifeMonths));
    }

    public async Task<AssetRegister> RegisterAsync(CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var categories = await db.Set<AssetCategory>().AsNoTracking().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Id, ct);
        var assets = await db.Set<FixedAsset>().AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(a => a.AssetNo).ToListAsync(ct);
        var live = assets.Where(a => a.Status is AssetStatus.Active or AssetStatus.FullyDepreciated).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assetAccounts = categories.Values.Select(c => c.AssetAccountId).Distinct().ToList();
        var accumulatedAccounts = categories.Values.Select(c => c.AccumulatedAccountId).Distinct().ToList();
        var balances = await (from l in db.Set<JournalLine>().AsNoTracking()
                              join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                              where e.CompanyId == companyId && (assetAccounts.Contains(l.AccountId) || accumulatedAccounts.Contains(l.AccountId))
                              group l by l.AccountId into g
                              select new { g.Key, Net = g.Sum(x => x.DebitBase - x.CreditBase) }).ToListAsync(ct);
        var ledgerCost = balances.Where(b => assetAccounts.Contains(b.Key)).Sum(b => b.Net);
        var ledgerAccumulated = -balances.Where(b => accumulatedAccounts.Contains(b.Key)).Sum(b => b.Net);
        return new AssetRegister(today, assets.Select(a => ToDto(a, categories)).ToList(), live.Sum(a => a.Cost), live.Sum(a => a.AccumulatedDepreciation),
            live.Sum(a => a.NetBookValue), ledgerCost, ledgerAccumulated);
    }

    internal static FixedAssetDto ToDto(FixedAsset a, IReadOnlyDictionary<Guid, AssetCategory> categories) =>
        new(a.Id, a.AssetNo, a.NameEn, a.NameAr, a.CategoryId, categories.TryGetValue(a.CategoryId, out var c) ? c.Code : "", a.Status.ToString(), a.AcquisitionDate,
            a.DepreciationStart, a.Cost, a.SalvageValue, a.UsefulLifeMonths, Money.Round(a.Depreciable / a.UsefulLifeMonths), a.AccumulatedDepreciation, a.NetBookValue,
            a.CostCenterId, a.Location, a.SerialNo, a.DisposalDate, a.DisposalProceeds, a.Notes);

    private async Task<FixedAsset> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<FixedAsset>().SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Fixed asset", id);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

public sealed record ReverseRunRequest(DateOnly Date, string Reason);

internal static class FixedAssetEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/finance/assets").WithTags("Fixed assets").RequireAuthorization();
        g.MapGet("/categories", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await db.Set<AssetCategory>().AsNoTracking().Where(c => c.CompanyId == t.RequireCompanyId()).OrderBy(c => c.Code).ToListAsync(ct)).Select(ToDto))
            .RequirePermission(FinancePermissions.AssetsRead);
        g.MapPost("/categories", async (AssetCategoryRequest r, FixedAssetService s, CancellationToken ct) => ToDto(await s.SaveCategoryAsync(null, r, ct)))
            .RequirePermission(FinancePermissions.AssetsManage);
        g.MapPut("/categories/{id:guid}", async (Guid id, AssetCategoryRequest r, FixedAssetService s, CancellationToken ct) => ToDto(await s.SaveCategoryAsync(id, r, ct)))
            .RequirePermission(FinancePermissions.AssetsManage);

        g.MapGet("", (FixedAssetService s, CancellationToken ct) => s.RegisterAsync(ct)).RequirePermission(FinancePermissions.AssetsRead);
        g.MapPost("", async (FixedAssetRequest r, FixedAssetService s, ErpDbContext db, CancellationToken ct) => await DtoAsync(db, await s.SaveAsync(null, r, ct), ct))
            .RequirePermission(FinancePermissions.AssetsManage);
        g.MapPut("/{id:guid}", async (Guid id, FixedAssetRequest r, FixedAssetService s, ErpDbContext db, CancellationToken ct) => await DtoAsync(db, await s.SaveAsync(id, r, ct), ct))
            .RequirePermission(FinancePermissions.AssetsManage);
        g.MapPost("/{id:guid}/capitalize", async (Guid id, CapitalizeRequest r, FixedAssetService s, ErpDbContext db, CancellationToken ct) =>
            await DtoAsync(db, await s.CapitalizeAsync(id, r, ct), ct)).RequirePermission(FinancePermissions.AssetsManage);
        g.MapPost("/{id:guid}/dispose", async (Guid id, DisposalRequest r, FixedAssetService s, ErpDbContext db, CancellationToken ct) =>
            await DtoAsync(db, await s.DisposeAsync(id, r, ct), ct)).RequirePermission(FinancePermissions.AssetsManage);

        g.MapGet("/depreciation-runs", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await db.Set<DepreciationRun>().AsNoTracking().Where(x => x.CompanyId == t.RequireCompanyId()).OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
                .Select(x => new DepreciationRunDto(x.Id, x.RunNo, x.Year, x.Month, x.PostingDate, x.Total, x.Status.ToString(), x.JournalEntryId, x.Lines.Count))
                .ToListAsync(ct)).RequirePermission(FinancePermissions.AssetsRead);
        g.MapPost("/depreciation-runs", async (DepreciationRequest r, FixedAssetService s, CancellationToken ct) =>
        {
            var run = await s.RunAsync(r, ct);
            return new DepreciationRunDto(run.Id, run.RunNo, run.Year, run.Month, run.PostingDate, run.Total, run.Status.ToString(), run.JournalEntryId, run.Lines.Count);
        }).RequirePermission(FinancePermissions.AssetsManage);
        g.MapPost("/depreciation-runs/{id:guid}/reverse", async (Guid id, ReverseRunRequest r, FixedAssetService s, CancellationToken ct) =>
        {
            var run = await s.ReverseRunAsync(id, r.Reason, r.Date, ct);
            return new DepreciationRunDto(run.Id, run.RunNo, run.Year, run.Month, run.PostingDate, run.Total, run.Status.ToString(), run.JournalEntryId, run.Lines.Count);
        }).RequirePermission(FinancePermissions.AssetsManage);
    }

    private static AssetCategoryDto ToDto(AssetCategory c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.UsefulLifeMonths, c.AssetAccountId, c.AccumulatedAccountId,
        c.ExpenseAccountId, c.IsActive);

    private static async Task<FixedAssetDto> DtoAsync(ErpDbContext db, FixedAsset a, CancellationToken ct)
    {
        var categories = await db.Set<AssetCategory>().AsNoTracking().Where(c => c.Id == a.CategoryId).ToDictionaryAsync(c => c.Id, ct);
        return FixedAssetService.ToDto(a, categories);
    }
}

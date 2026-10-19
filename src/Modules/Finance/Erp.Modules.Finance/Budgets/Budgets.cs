using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Finance.Budgets;

public enum BudgetStatus { Draft, Approved }

public sealed class Budget : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid FiscalYearId { get; init; }
    public required string Name { get; set; }
    public BudgetStatus Status { get; set; } = BudgetStatus.Draft;
    public string? Notes { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public List<BudgetLine> Lines { get; init; } = [];
}

public sealed class BudgetLine : TenantEntity
{
    public Guid BudgetId { get; init; }
    public Guid AccountId { get; init; }
    public Guid? CostCenterId { get; init; }
    public int PeriodNo { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> b)
    {
        b.ToTable("budget", "fin");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.BudgetId);
    }
}

internal sealed class BudgetLineConfiguration : IEntityTypeConfiguration<BudgetLine>
{
    public void Configure(EntityTypeBuilder<BudgetLine> b) => b.ToTable("budget_line", "fin");
}

public sealed record BudgetRequest(Guid FiscalYearId, string Name, string? Notes);

/// <summary>One row of the budget grid: twelve monthly amounts (fiscal periods 1–12), presented like the statements (income and expense positive).</summary>
public sealed record BudgetRowRequest(Guid AccountId, Guid? CostCenterId, IReadOnlyList<decimal> Months);

public sealed record BudgetRowDto(Guid AccountId, string Code, string NameEn, string NameAr, string AccountType, Guid? CostCenterId, IReadOnlyList<decimal> Months,
    decimal Total);

public sealed record BudgetDto(Guid Id, Guid FiscalYearId, string FiscalYearName, DateOnly StartDate, string Name, string Status, string? Notes,
    IReadOnlyList<BudgetRowDto> Rows, decimal TotalIncome, decimal TotalExpense);

public sealed record BudgetSummaryDto(Guid Id, Guid FiscalYearId, string FiscalYearName, string Name, string Status, decimal TotalIncome, decimal TotalExpense);

public sealed record VarianceRow(Guid AccountId, string Code, string NameEn, string NameAr, string AccountType, decimal Budget, decimal Actual, decimal Variance,
    decimal? VariancePercent, bool Favourable);

public sealed record BudgetVsActual(Guid BudgetId, string Name, int FromPeriod, int ToPeriod, DateOnly From, DateOnly To, Guid? CostCenterId,
    IReadOnlyList<VarianceRow> Rows, decimal BudgetProfit, decimal ActualProfit);

/// <summary>
/// Budgets for income and expense accounts per month. Amounts are entered as on the income statement (income and expense both
/// positive); the comparison takes actuals for the same months, credit-positive for income and debit-positive for expenses.
/// </summary>
internal sealed class BudgetService(ErpDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<Budget> CreateAsync(BudgetRequest r, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        if (!await db.Set<FiscalYear>().AnyAsync(y => y.Id == r.FiscalYearId && y.CompanyId == companyId, ct)) throw new NotFoundException("Fiscal year", r.FiscalYearId);
        var name = Guard.Required(r.Name, "name").Trim();
        if (await db.Set<Budget>().AnyAsync(b => b.CompanyId == companyId && b.FiscalYearId == r.FiscalYearId && b.Name == name, ct))
            throw new ConflictException("fin.budget_exists", "A budget with this name exists for the year.", "توجد موازنة بهذا الاسم لهذه السنة.");
        var budget = new Budget { CompanyId = companyId, FiscalYearId = r.FiscalYearId, Name = name, Notes = r.Notes?.Trim() };
        db.Add(budget);
        await db.SaveChangesAsync(ct);
        return budget;
    }

    /// <summary>Replaces the grid. Only draft budgets change; an approved budget is the baseline for variance reports.</summary>
    public Task SaveLinesAsync(Guid id, IReadOnlyList<BudgetRowRequest> rows, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var budget = await FindAsync(id, token);
            if (budget.Status != BudgetStatus.Draft)
                throw new ConflictException("fin.budget_approved", "Approved budgets cannot be changed; reopen it first.", "لا يمكن تعديل موازنة معتمدة؛ أعد فتحها أولاً.");
            var accountIds = rows.Select(r => r.AccountId).Distinct().ToList();
            var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id) && a.CompanyId == budget.CompanyId).ToDictionaryAsync(a => a.Id, token);
            if (rows.GroupBy(r => (r.AccountId, r.CostCenterId)).Any(g => g.Count() > 1))
                throw new BusinessRuleException("fin.budget_duplicate", "Each account and cost centre may appear once.", "يجب أن يظهر كل حساب ومركز تكلفة مرة واحدة.");
            db.RemoveRange(budget.Lines);
            budget.Lines.Clear();
            await db.SaveChangesAsync(token);
            foreach (var row in rows)
            {
                if (!accounts.TryGetValue(row.AccountId, out var account) || !account.IsPostable) throw new NotFoundException("Account", row.AccountId);
                if (!account.IsProfitAndLoss)
                    throw new BusinessRuleException("fin.budget_account", $"Budget income and expense accounts only ({account.Code} is not).",
                        $"تُعد الموازنة لحسابات الإيرادات والمصروفات فقط ({account.Code} ليس كذلك).");
                if (row.Months is not { Count: 12 })
                    throw new BusinessRuleException("fin.budget_months", "Give twelve monthly amounts.", "أدخل اثني عشر مبلغاً شهرياً.");
                for (var p = 0; p < 12; p++)
                {
                    var amount = Money.Round(row.Months[p]);
                    if (amount == 0) continue;
                    var line = new BudgetLine { BudgetId = budget.Id, AccountId = account.Id, CostCenterId = row.CostCenterId, PeriodNo = p + 1, Amount = amount };
                    budget.Lines.Add(line);
                    db.Add(line);
                }
            }
            await db.SaveChangesAsync(token);
        }, ct);

    public async Task<Budget> SetStatusAsync(Guid id, BudgetStatus status, CancellationToken ct)
    {
        var budget = await FindAsync(id, ct);
        budget.Status = status;
        budget.ApprovedBy = status == BudgetStatus.Approved ? tenant.ActingUserId : null;
        budget.ApprovedAt = status == BudgetStatus.Approved ? clock.UtcNow : null;
        await db.SaveChangesAsync(ct);
        return budget;
    }

    public async Task<BudgetDto> GetAsync(Guid id, CancellationToken ct)
    {
        var budget = await FindAsync(id, ct);
        var year = await db.Set<FiscalYear>().AsNoTracking().SingleAsync(y => y.Id == budget.FiscalYearId, ct);
        var ids = budget.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var rows = budget.Lines.GroupBy(l => (l.AccountId, l.CostCenterId)).Select(g =>
        {
            var months = Enumerable.Range(1, 12).Select(p => g.Where(l => l.PeriodNo == p).Sum(l => l.Amount)).ToList();
            var a = accounts[g.Key.AccountId];
            return new BudgetRowDto(a.Id, a.Code, a.NameEn, a.NameAr, a.AccountType.ToString(), g.Key.CostCenterId, months, months.Sum());
        }).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        return new BudgetDto(budget.Id, year.Id, year.Name, year.StartDate, budget.Name, budget.Status.ToString(), budget.Notes, rows,
            rows.Where(r => r.AccountType == nameof(AccountType.Income)).Sum(r => r.Total), rows.Where(r => r.AccountType == nameof(AccountType.Expense)).Sum(r => r.Total));
    }

    public async Task<BudgetVsActual> VarianceAsync(Guid id, int fromPeriod, int toPeriod, Guid? costCenterId, CancellationToken ct)
    {
        if (fromPeriod is < 1 or > 12 || toPeriod < fromPeriod || toPeriod > 12)
            throw new BusinessRuleException("fin.budget_periods", "Choose months 1 to 12, from before to.", "اختر الأشهر من 1 إلى 12 بالترتيب.");
        var budget = await FindAsync(id, ct);
        var year = await db.Set<FiscalYear>().AsNoTracking().SingleAsync(y => y.Id == budget.FiscalYearId, ct);
        var periodStart = year.StartDate.AddMonths(fromPeriod - 1);
        var periodEnd = year.StartDate.AddMonths(toPeriod).AddDays(-1);
        var budgetLines = budget.Lines.Where(l => l.PeriodNo >= fromPeriod && l.PeriodNo <= toPeriod && (costCenterId == null || l.CostCenterId == costCenterId)).ToList();
        var companyId = tenant.RequireCompanyId();
        var actual = await (from l in db.Set<JournalLine>().AsNoTracking()
                            join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                            join a in db.Set<Account>() on l.AccountId equals a.Id
                            where e.CompanyId == companyId && e.PostingDate >= periodStart && e.PostingDate <= periodEnd
                                  && (a.AccountType == AccountType.Income || a.AccountType == AccountType.Expense)
                                  && (costCenterId == null || l.CostCenterId == costCenterId)
                            group l by l.AccountId into g
                            select new { g.Key, Net = g.Sum(x => x.DebitBase - x.CreditBase) }).ToDictionaryAsync(x => x.Key, x => x.Net, ct);
        var ids = budgetLines.Select(l => l.AccountId).Concat(actual.Keys).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var rows = ids.Select(accountId =>
        {
            var a = accounts[accountId];
            var income = a.AccountType == AccountType.Income;
            var b = budgetLines.Where(l => l.AccountId == accountId).Sum(l => l.Amount);
            var act = income ? -actual.GetValueOrDefault(accountId) : actual.GetValueOrDefault(accountId);
            var variance = act - b;
            return new VarianceRow(a.Id, a.Code, a.NameEn, a.NameAr, a.AccountType.ToString(), b, act, variance,
                b == 0 ? null : Math.Round(variance * 100m / Math.Abs(b), 1, MidpointRounding.AwayFromZero), income ? variance >= 0 : variance <= 0);
        }).Where(r => r.Budget != 0 || r.Actual != 0).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        decimal Profit(Func<VarianceRow, decimal> pick) =>
            rows.Where(r => r.AccountType == nameof(AccountType.Income)).Sum(pick) - rows.Where(r => r.AccountType == nameof(AccountType.Expense)).Sum(pick);
        return new BudgetVsActual(budget.Id, budget.Name, fromPeriod, toPeriod, periodStart, periodEnd, costCenterId, rows, Profit(r => r.Budget), Profit(r => r.Actual));
    }

    private async Task<Budget> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<Budget>().Include(b => b.Lines).SingleOrDefaultAsync(b => b.Id == id && b.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Budget", id);
}

internal static class BudgetEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/finance/budgets").WithTags("Budgets").RequireAuthorization();
        g.MapGet("", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await (from b in db.Set<Budget>().AsNoTracking()
                   join y in db.Set<FiscalYear>() on b.FiscalYearId equals y.Id
                   where b.CompanyId == t.RequireCompanyId()
                   orderby y.StartDate descending, b.Name
                   select new BudgetSummaryDto(b.Id, y.Id, y.Name, b.Name, b.Status.ToString(),
                       db.Set<BudgetLine>().Where(l => l.BudgetId == b.Id && db.Set<Account>().Any(a => a.Id == l.AccountId && a.AccountType == AccountType.Income)).Sum(l => (decimal?)l.Amount) ?? 0,
                       db.Set<BudgetLine>().Where(l => l.BudgetId == b.Id && db.Set<Account>().Any(a => a.Id == l.AccountId && a.AccountType == AccountType.Expense)).Sum(l => (decimal?)l.Amount) ?? 0))
                .ToListAsync(ct)).RequirePermission(FinancePermissions.ReportsRead);
        g.MapGet("/{id:guid}", (Guid id, BudgetService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(FinancePermissions.ReportsRead);
        g.MapPost("", async (BudgetRequest r, BudgetService s, CancellationToken ct) => await s.GetAsync((await s.CreateAsync(r, ct)).Id, ct))
            .RequirePermission(FinancePermissions.BudgetsManage);
        g.MapPut("/{id:guid}/lines", async (Guid id, List<BudgetRowRequest> rows, BudgetService s, CancellationToken ct) =>
        {
            await s.SaveLinesAsync(id, rows, ct);
            return await s.GetAsync(id, ct);
        }).RequirePermission(FinancePermissions.BudgetsManage);
        g.MapPost("/{id:guid}/approve", async (Guid id, BudgetService s, CancellationToken ct) => await s.GetAsync((await s.SetStatusAsync(id, BudgetStatus.Approved, ct)).Id, ct))
            .RequirePermission(FinancePermissions.BudgetsManage);
        g.MapPost("/{id:guid}/reopen", async (Guid id, BudgetService s, CancellationToken ct) => await s.GetAsync((await s.SetStatusAsync(id, BudgetStatus.Draft, ct)).Id, ct))
            .RequirePermission(FinancePermissions.BudgetsManage);
        g.MapGet("/{id:guid}/variance", (Guid id, BudgetService s, CancellationToken ct, int fromPeriod = 1, int toPeriod = 12, Guid? costCenterId = null) =>
            s.VarianceAsync(id, fromPeriod, toPeriod, costCenterId, ct)).RequirePermission(FinancePermissions.ReportsRead);
    }
}

using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Reports;

public sealed record StatementAccount(Guid AccountId, string Code, string NameEn, string NameAr, decimal Amount, decimal? Compare);

public sealed record StatementSection(string Code, string NameEn, string NameAr, string Kind, IReadOnlyList<StatementAccount> Accounts, decimal Total,
    decimal? CompareTotal);

public sealed record ProfitAndLoss(DateOnly From, DateOnly To, DateOnly? CompareFrom, DateOnly? CompareTo, IReadOnlyList<StatementSection> Sections,
    decimal Revenue, decimal? CompareRevenue, decimal GrossProfit, decimal? CompareGrossProfit, decimal NetProfit, decimal? CompareNetProfit);

public sealed record BalanceSheet(DateOnly AsOf, DateOnly? CompareAsOf, IReadOnlyList<StatementSection> Sections, decimal TotalAssets,
    decimal? CompareTotalAssets, decimal TotalLiabilities, decimal? CompareTotalLiabilities, decimal TotalEquity, decimal? CompareTotalEquity,
    decimal ProfitForYear, decimal? CompareProfitForYear, bool IsBalanced);

/// <summary>
/// Income statement and balance sheet built from the chart of accounts: each section is a group directly under a top-level
/// account (e.g. 51 Cost of Sales, 11 Current Assets), and accounts posted directly under a top-level account form a section
/// named after it. Income and liabilities/equity are shown credit-positive, expenses and assets debit-positive. The cost-of-sales
/// group (subtype <c>cost_of_sales</c>) gives gross profit. Prior years' results sit in retained earnings, the current year's
/// result is a separate equity line, so the balance sheet balances without a closing entry.
/// </summary>
internal sealed class FinancialStatements(ErpDbContext db, ITenantContext tenant)
{
    public async Task<ProfitAndLoss> ProfitAndLossAsync(DateOnly from, DateOnly to, DateOnly? compareFrom, DateOnly? compareTo, CancellationToken ct)
    {
        Range(from, to);
        if ((compareFrom is null) != (compareTo is null))
            throw new BusinessRuleException("report.compare_range", "Give both comparison dates or neither.", "أدخل تاريخي المقارنة معاً أو اتركهما.");
        if (compareFrom is { } cf && compareTo is { } ctTo) Range(cf, ctTo);

        var chart = await ChartAsync(ct);
        var current = await MovementsAsync(from, to, ct);
        var compare = compareFrom is { } a && compareTo is { } b ? await MovementsAsync(a, b, ct) : null;
        var sections = Sections(chart, current, compare, acc => acc.IsProfitAndLoss);

        decimal Sum(Func<StatementSection, bool> which, bool comparing) =>
            sections.Where(which).Sum(s => (comparing ? s.CompareTotal ?? 0 : s.Total) * (s.Kind == "income" ? 1 : -1));
        bool Revenue(StatementSection s) => s.Kind == "income";
        bool UpToGross(StatementSection s) => s.Kind is "income" or "cost_of_sales";
        var comparing = compare is not null;
        return new ProfitAndLoss(from, to, compareFrom, compareTo, sections,
            Sum(Revenue, false), comparing ? Sum(Revenue, true) : null,
            Sum(UpToGross, false), comparing ? Sum(UpToGross, true) : null,
            Sum(_ => true, false), comparing ? Sum(_ => true, true) : null);
    }

    public async Task<BalanceSheet> BalanceSheetAsync(DateOnly asOf, DateOnly? compareAsOf, CancellationToken ct)
    {
        var chart = await ChartAsync(ct);
        var (current, profit) = await PositionAsync(chart, asOf, ct);
        Dictionary<Guid, decimal>? compare = null;
        decimal? compareProfit = null;
        if (compareAsOf is { } c) (compare, compareProfit) = await PositionAsync(chart, c, ct);
        var sections = Sections(chart, current, compare, acc => !acc.IsProfitAndLoss).ToList();

        // The current year's result belongs to equity until the year is closed.
        var equity = sections.FirstOrDefault(s => s.Kind == "equity");
        var profitLine = new StatementAccount(Guid.Empty, "", "Profit for the year", "ربح السنة", profit, compareProfit);
        if (equity is null)
        {
            var root = chart.Values.First(a => a.ParentId is null && a.AccountType == AccountType.Equity);
            sections.Add(new StatementSection(root.Code, root.NameEn, root.NameAr, "equity", [profitLine], profit, compareProfit));
        }
        else
            sections[sections.IndexOf(equity)] = equity with
            {
                Accounts = [.. equity.Accounts, profitLine], Total = equity.Total + profit,
                CompareTotal = equity.CompareTotal is { } t ? t + (compareProfit ?? 0) : null
            };

        decimal Total(string kind, bool comparing) => sections.Where(s => s.Kind == kind).Sum(s => comparing ? s.CompareTotal ?? 0 : s.Total);
        var comparing = compareAsOf is not null;
        var assets = Total("asset", false);
        var liabilities = Total("liability", false);
        var equityTotal = Total("equity", false);
        return new BalanceSheet(asOf, compareAsOf, sections, assets, comparing ? Total("asset", true) : null, liabilities,
            comparing ? Total("liability", true) : null, equityTotal, comparing ? Total("equity", true) : null, profit, compareProfit,
            assets == liabilities + equityTotal);
    }

    private static void Range(DateOnly from, DateOnly to)
    {
        if (to < from) throw new BusinessRuleException("report.invalid_range", "'to' must be on or after 'from'.", "يجب أن يكون تاريخ النهاية بعد تاريخ البداية.");
    }

    private async Task<Dictionary<Guid, Account>> ChartAsync(CancellationToken ct) =>
        await db.Set<Account>().AsNoTracking().Where(a => a.CompanyId == tenant.RequireCompanyId()).ToDictionaryAsync(a => a.Id, ct);

    /// <summary>Debit − credit per account for postings between two dates (inclusive).</summary>
    private async Task<Dictionary<Guid, decimal>> MovementsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var (periodStart, periodEnd) = (from, to);
        return await (from l in db.Set<JournalLine>().AsNoTracking()
                      join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                      where e.CompanyId == companyId && e.PostingDate >= periodStart && e.PostingDate <= periodEnd
                      group l by l.AccountId into g
                      select new { g.Key, Net = g.Sum(x => x.DebitBase - x.CreditBase) }).ToDictionaryAsync(x => x.Key, x => x.Net, ct);
    }

    /// <summary>Balance-sheet balances at a date, with prior years' results folded into retained earnings, and the current year's result.</summary>
    private async Task<(Dictionary<Guid, decimal> Balances, decimal ProfitForYear)> PositionAsync(Dictionary<Guid, Account> chart, DateOnly asOf, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var yearStart = await db.Set<FiscalYear>().AsNoTracking().Where(y => y.CompanyId == companyId && y.StartDate <= asOf && y.EndDate >= asOf)
            .Select(y => (DateOnly?)y.StartDate).SingleOrDefaultAsync(ct) ?? new DateOnly(asOf.Year, 1, 1);
        var all = await MovementsAsync(DateOnly.MinValue, asOf, ct);
        var thisYear = await MovementsAsync(yearStart, asOf, ct);

        var balances = all.Where(x => !chart[x.Key].IsProfitAndLoss).ToDictionary(x => x.Key, x => x.Value);
        var priorYears = all.Where(x => chart[x.Key].IsProfitAndLoss).Sum(x => x.Value) - thisYear.Where(x => chart[x.Key].IsProfitAndLoss).Sum(x => x.Value);
        if (priorYears != 0)
        {
            var retained = chart.Values.FirstOrDefault(a => a.Subtype == "retained_earnings")
                           ?? throw new BusinessRuleException("fin.retained_earnings_missing", "Mark an equity account as retained earnings.",
                               "حدد حساب حقوق ملكية كأرباح محتجزة.");
            balances[retained.Id] = balances.GetValueOrDefault(retained.Id) + priorYears;
        }
        // Debit-positive net result of the year; profit is credit, so negate.
        var profit = -thisYear.Where(x => chart[x.Key].IsProfitAndLoss).Sum(x => x.Value);
        return (balances, profit);
    }

    private static List<StatementSection> Sections(Dictionary<Guid, Account> chart, Dictionary<Guid, decimal> current, Dictionary<Guid, decimal>? compare,
        Func<Account, bool> include)
    {
        Account SectionOf(Account a)
        {
            // Walk up to the top-level account or to a direct child of it.
            var node = a;
            while (node.ParentId is { } parentId && chart[parentId].ParentId is not null) node = chart[parentId];
            if (node.ParentId is null) return node;
            // A group under a top-level account is a section; an account posted directly under one belongs to the top-level section.
            return node.IsPostable ? chart[node.ParentId.Value] : node;
        }

        var ids = current.Keys.Concat(compare?.Keys ?? Enumerable.Empty<Guid>()).Distinct().Where(id => include(chart[id]));
        return ids
            .Select(id => (Account: chart[id], Sign: Sign(chart[id]), Amount: current.GetValueOrDefault(id), Compare: compare?.GetValueOrDefault(id)))
            .Where(x => x.Amount != 0 || x.Compare is not (null or 0))
            .GroupBy(x => SectionOf(x.Account))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g =>
            {
                var accounts = g.OrderBy(x => x.Account.Code, StringComparer.Ordinal)
                    .Select(x => new StatementAccount(x.Account.Id, x.Account.Code, x.Account.NameEn, x.Account.NameAr, x.Sign * x.Amount,
                        compare is null ? null : x.Sign * (x.Compare ?? 0)))
                    .ToList();
                return new StatementSection(g.Key.Code, g.Key.NameEn, g.Key.NameAr, Kind(g.Key), accounts, accounts.Sum(a => a.Amount),
                    compare is null ? null : accounts.Sum(a => a.Compare ?? 0));
            }).ToList();
    }

    /// <summary>Income, liabilities and equity are presented credit-positive.</summary>
    private static int Sign(Account a) => a.AccountType is AccountType.Asset or AccountType.Expense ? 1 : -1;

    private static string Kind(Account section) => section.AccountType switch
    {
        AccountType.Income => "income",
        AccountType.Expense => section.Subtype == "cost_of_sales" ? "cost_of_sales" : "expense",
        AccountType.Asset => "asset",
        AccountType.Liability => "liability",
        _ => "equity"
    };
}

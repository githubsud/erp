using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Finance.Posting;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Reports;

public sealed record CashFlowLine(string Code, string NameEn, string NameAr, decimal Amount);

public sealed record CashFlowSection(string Kind, IReadOnlyList<CashFlowLine> Lines, decimal Total);

public sealed record CashFlowStatement(DateOnly From, DateOnly To, decimal NetProfit, IReadOnlyList<CashFlowSection> Sections, decimal NetChange,
    decimal OpeningCash, decimal ClosingCash, bool Reconciles);

public sealed record ChecklistItem(string Code, string TextEn, string TextAr, bool Ok, bool Blocking, string? Detail);

public sealed record YearEndChecklist(Guid FiscalYearId, string Name, DateOnly EndDate, IReadOnlyList<ChecklistItem> Items, bool CanClose);

/// <summary>
/// Cash-flow statement (indirect method) from the chart of accounts. Cash is every bank and cash account. Operating: net profit,
/// changes in current assets and in liabilities (provisions included), and non-cash write-downs of non-current assets
/// (accumulated depreciation, credit-normal); investing: other non-current assets; financing: equity and liabilities marked
/// <c>loan</c>. Each line is the cash effect of the account's movement, so the sections add up to the change in cash.
/// </summary>
internal sealed class CashFlowReport(ErpDbContext db, ITenantContext tenant)
{
    public async Task<CashFlowStatement> BuildAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) throw new BusinessRuleException("report.invalid_range", "'to' must be on or after 'from'.", "يجب أن يكون تاريخ النهاية بعد تاريخ البداية.");
        var companyId = tenant.RequireCompanyId();
        var chart = await db.Set<Account>().AsNoTracking().Where(a => a.CompanyId == companyId).ToDictionaryAsync(a => a.Id, ct);
        var (periodStart, periodEnd) = (from, to);
        var movements = await (from l in db.Set<JournalLine>().AsNoTracking()
                               join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                               where e.CompanyId == companyId && e.PostingDate >= periodStart && e.PostingDate <= periodEnd
                               group l by l.AccountId into g
                               select new { g.Key, Net = g.Sum(x => x.DebitBase - x.CreditBase) }).ToDictionaryAsync(x => x.Key, x => x.Net, ct);
        var before = await (from l in db.Set<JournalLine>().AsNoTracking()
                            join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                            where e.CompanyId == companyId && e.PostingDate < periodStart
                            group l by l.AccountId into g
                            select new { g.Key, Net = g.Sum(x => x.DebitBase - x.CreditBase) }).ToDictionaryAsync(x => x.Key, x => x.Net, ct);

        bool IsCash(Account a) => a.Subtype is "bank" or "cash";
        string TopGroup(Account a)
        {
            var node = a;
            while (node.ParentId is { } p && chart[p].ParentId is not null) node = chart[p];
            return node.Code;
        }
        string Kind(Account a) => a.AccountType switch
        {
            AccountType.Equity => "financing",
            AccountType.Liability => a.Subtype == "loan" ? "financing" : "operating",
            AccountType.Asset when TopGroup(a).StartsWith("11", StringComparison.Ordinal) => "operating",
            AccountType.Asset => a.NormalBalance == NormalBalance.Credit ? "operating" : "investing",
            _ => "operating"
        };

        var netProfit = -movements.Where(m => chart[m.Key].IsProfitAndLoss).Sum(m => m.Value);
        var lines = movements.Where(m => !chart[m.Key].IsProfitAndLoss && !IsCash(chart[m.Key]) && m.Value != 0)
            .Select(m => (Kind: Kind(chart[m.Key]), Line: new CashFlowLine(chart[m.Key].Code, chart[m.Key].NameEn, chart[m.Key].NameAr, -m.Value)))
            .ToList();
        var operating = new List<CashFlowLine> { new("", "Net profit for the period", "صافي ربح الفترة", netProfit) };
        operating.AddRange(lines.Where(x => x.Kind == "operating").Select(x => x.Line).OrderBy(l => l.Code, StringComparer.Ordinal));
        var sections = new List<CashFlowSection>
        {
            new("operating", operating, operating.Sum(l => l.Amount)),
            Section("investing", lines),
            Section("financing", lines)
        };
        var opening = before.Where(b => IsCash(chart[b.Key])).Sum(b => b.Value);
        var change = movements.Where(m => IsCash(chart[m.Key])).Sum(m => m.Value);
        var net = sections.Sum(s => s.Total);
        return new CashFlowStatement(from, to, netProfit, sections, net, opening, opening + change, net == change);
    }

    private static CashFlowSection Section(string kind, List<(string Kind, CashFlowLine Line)> lines)
    {
        var list = lines.Where(x => x.Kind == kind).Select(x => x.Line).OrderBy(l => l.Code, StringComparer.Ordinal).ToList();
        return new CashFlowSection(kind, list, list.Sum(l => l.Amount));
    }
}

/// <summary>
/// Year-end close. The statements carry prior years' results into retained earnings by themselves, so closing a year means
/// locking it: every period is closed after the checklist passes, and nothing can be posted into it any more.
/// </summary>
internal sealed class YearEndService(ErpDbContext db, ITenantContext tenant, FiscalCalendar calendar)
{
    public async Task<YearEndChecklist> ChecklistAsync(Guid yearId, CancellationToken ct)
    {
        var year = await FindAsync(yearId, ct);
        var companyId = tenant.RequireCompanyId();
        var items = new List<ChecklistItem>();

        var hasNext = await db.Set<FiscalYear>().AnyAsync(y => y.CompanyId == companyId && y.StartDate == year.EndDate.AddDays(1), ct);
        items.Add(new ChecklistItem("next_year", "The next fiscal year is set up", "تم إعداد السنة المالية التالية", hasNext, false,
            hasNext ? null : "Created automatically when the year is closed."));

        var unreconciled = await (from l in db.Set<BankStatementLine>()
                                  join s in db.Set<BankStatement>() on l.BankStatementId equals s.Id
                                  where s.CompanyId == companyId && l.Status == BankLineStatus.Unmatched && l.TxnDate <= year.EndDate
                                  select l.Id).CountAsync(ct);
        items.Add(new ChecklistItem("bank", "Bank statements reconciled to year end", "تمت مطابقة كشوف البنك حتى نهاية السنة", unreconciled == 0, false,
            unreconciled == 0 ? null : $"{unreconciled} unmatched statement line(s)"));

        var balances = await (from l in db.Set<JournalLine>()
                              join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                              join a in db.Set<Account>() on l.AccountId equals a.Id
                              where e.CompanyId == companyId && e.PostingDate <= year.EndDate && (a.Code == "1140" || a.Code == "2120" || a.Code == "1190")
                              group l by a.Code into g
                              select new { g.Key, Net = g.Sum(x => x.DebitBase - x.CreditBase) }).ToDictionaryAsync(x => x.Key, x => x.Net, ct);
        var pdc = balances.GetValueOrDefault("1140") != 0 || balances.GetValueOrDefault("2120") != 0;
        items.Add(new ChecklistItem("pdc", "Post-dated cheques reviewed", "تمت مراجعة الشيكات الآجلة", !pdc, false,
            pdc ? $"Held: {balances.GetValueOrDefault("1140"):N2} received, {-balances.GetValueOrDefault("2120"):N2} issued" : null));
        var landed = balances.GetValueOrDefault("1190");
        items.Add(new ChecklistItem("landed_cost", "Landed cost charges allocated", "تم توزيع تكاليف الاستيراد", landed == 0, false,
            landed == 0 ? null : $"{landed:N2} waiting in landed cost clearing"));

        var unbalanced = await db.Set<JournalEntry>().Where(e => e.CompanyId == companyId && e.PostingDate >= year.StartDate && e.PostingDate <= year.EndDate)
            .Select(e => db.Set<JournalLine>().Where(l => l.JournalEntryId == e.Id).Sum(l => l.DebitBase - l.CreditBase))
            .AnyAsync(net => net != 0, ct);
        items.Add(new ChecklistItem("balanced", "Every journal entry balances", "جميع القيود متوازنة", !unbalanced, true, null));

        return new YearEndChecklist(year.Id, year.Name, year.EndDate, items, year.Status == FiscalStatus.Open && items.Where(i => i.Blocking).All(i => i.Ok));
    }

    public Task<FiscalYear> CloseAsync(Guid yearId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var checklist = await ChecklistAsync(yearId, token);
            if (!checklist.CanClose)
                throw new BusinessRuleException("fin.year_close_blocked", "Fix the blocking checklist items first.", "عالج بنود القائمة المانعة أولاً.");
            var year = await FindAsync(yearId, token);
            var companyId = tenant.RequireCompanyId();
            if (!await db.Set<FiscalYear>().AnyAsync(y => y.CompanyId == companyId && y.StartDate == year.EndDate.AddDays(1), token))
                await calendar.CreateYearAsync(year.EndDate.AddDays(1), token);
            foreach (var p in year.Periods) p.Status = FiscalStatus.Closed;
            year.Status = FiscalStatus.Closed;
            await db.SaveChangesAsync(token);
            return year;
        }, ct);

    public Task<FiscalYear> ReopenAsync(Guid yearId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var year = await FindAsync(yearId, token);
            if (await db.Set<FiscalYear>().AnyAsync(y => y.CompanyId == year.CompanyId && y.StartDate > year.EndDate && y.Status == FiscalStatus.Closed, token))
                throw new BusinessRuleException("fin.year_reopen_order", "Reopen the later closed years first.", "أعد فتح السنوات المغلقة اللاحقة أولاً.");
            foreach (var p in year.Periods) p.Status = FiscalStatus.Open;
            year.Status = FiscalStatus.Open;
            await db.SaveChangesAsync(token);
            return year;
        }, ct);

    private async Task<FiscalYear> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<FiscalYear>().Include(y => y.Periods).SingleOrDefaultAsync(y => y.Id == id && y.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Fiscal year", id);
}

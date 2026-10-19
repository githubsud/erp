using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Posting;

internal sealed class FiscalCalendar(ErpDbContext db, ITenantContext tenant)
{
    public async Task<FiscalPeriod> GetOpenPeriodAsync(DateOnly date, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var period = await db.Set<FiscalPeriod>().AsNoTracking()
            .SingleOrDefaultAsync(p => p.CompanyId == companyId && p.StartDate <= date && p.EndDate >= date, ct)
            ?? throw new BusinessRuleException("fin.period_missing", $"No fiscal period exists for {date:yyyy-MM-dd}. Create the fiscal year first.",
                $"لا توجد فترة مالية للتاريخ {date:yyyy-MM-dd}. أنشئ السنة المالية أولاً.");
        if (period.Status != FiscalStatus.Open)
            throw new BusinessRuleException("fin.period_closed", $"The fiscal period containing {date:yyyy-MM-dd} is closed.",
                $"الفترة المالية التي تتضمن {date:yyyy-MM-dd} مغلقة.");
        return period;
    }

    /// <summary>Creates a fiscal year of 12 monthly periods starting on <paramref name="start"/>.</summary>
    public async Task<FiscalYear> CreateYearAsync(DateOnly start, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        if (start.Day != 1)
            throw new BusinessRuleException("fin.fiscal_year_start", "A fiscal year must start on the first day of a month.", "يجب أن تبدأ السنة المالية في أول يوم من الشهر.");
        var end = start.AddYears(1).AddDays(-1);
        if (await db.Set<FiscalYear>().AnyAsync(y => y.CompanyId == companyId && y.StartDate <= end && y.EndDate >= start, ct))
            throw new ConflictException("fin.fiscal_year_overlap", "This fiscal year overlaps an existing one.", "السنة المالية تتداخل مع سنة موجودة.");

        var year = new FiscalYear
        {
            CompanyId = companyId,
            Name = start.Month == 1 ? start.Year.ToString() : $"{start.Year}/{end.Year}",
            StartDate = start,
            EndDate = end
        };
        for (var i = 0; i < 12; i++)
        {
            var periodStart = start.AddMonths(i);
            year.Periods.Add(new FiscalPeriod
            {
                CompanyId = companyId,
                FiscalYearId = year.Id,
                PeriodNo = i + 1,
                StartDate = periodStart,
                EndDate = periodStart.AddMonths(1).AddDays(-1)
            });
        }
        db.Add(year);
        return year;
    }
}

internal sealed class PostingRuleRegistry(ErpDbContext db, ITenantContext tenant) : IPostingRuleRegistry
{
    public async Task EnsureRuleAsync(string accountRole, Guid? roleKey, string accountCode, CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        var accountId = await db.Set<Account>().Where(a => a.CompanyId == companyId && a.Code == accountCode)
            .Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct)
            ?? db.ChangeTracker.Entries<Account>().Select(e => e.Entity).Where(a => a.CompanyId == companyId && a.Code == accountCode)
                .Select(a => (Guid?)a.Id).SingleOrDefault()
            ?? throw new BusinessRuleException("fin.account_not_found", $"Account {accountCode} does not exist.", $"الحساب {accountCode} غير موجود.");

        var rule = await db.Set<PostingRule>()
            .SingleOrDefaultAsync(r => r.CompanyId == companyId && r.AccountRole == accountRole && r.RoleKey == roleKey, ct);
        if (rule is null)
            db.Add(new PostingRule { CompanyId = companyId, AccountRole = accountRole, RoleKey = roleKey, AccountId = accountId });
        else
            rule.AccountId = accountId;
    }
}

internal sealed class FinanceQueries(ErpDbContext db, ITenantContext tenant) : IFinanceQueries
{
    public async Task<IReadOnlyList<ProjectLedgerLine>> GetProjectLedgerAsync(Guid projectId, CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        return await (from l in db.Set<JournalLine>().AsNoTracking()
                      join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                      join a in db.Set<Account>() on l.AccountId equals a.Id
                      where e.CompanyId == companyId && l.ProjectId == projectId && (a.AccountType == AccountType.Income || a.AccountType == AccountType.Expense)
                      group l by new { a.Id, a.Code, a.NameEn, a.NameAr, a.AccountType } into g
                      orderby g.Key.Code
                      select new ProjectLedgerLine(g.Key.Id, g.Key.Code, g.Key.NameEn, g.Key.NameAr, g.Key.AccountType == AccountType.Income,
                          g.Sum(x => x.DebitBase - x.CreditBase))).ToListAsync(ct);
    }

    public async Task<AccountSummary?> FindPostableAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        return await db.Set<Account>().AsNoTracking()
            .Where(a => a.Id == accountId && a.CompanyId == companyId && a.IsPostable && a.IsActive)
            .Select(a => new AccountSummary(a.Id, a.Code, a.NameEn, a.NameAr, a.Subtype))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<decimal> GetRoleBalanceAsync(string accountRole, DateOnly asOf, CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        var rules = await db.Set<PostingRule>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.AccountRole == accountRole && r.RoleKey == null).ToListAsync(ct);
        var accountId = PostingService.Resolve(rules, accountRole, null);
        return await (from l in db.Set<JournalLine>()
                      join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                      where l.AccountId == accountId && e.PostingDate <= asOf
                      select l.DebitBase - l.CreditBase).SumAsync(ct);
    }

    public async Task<decimal> GetPartyBalanceAsync(string accountRole, PartyType partyType, Guid partyId, DateOnly asOf, CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        var rules = await db.Set<PostingRule>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.AccountRole == accountRole && r.RoleKey == null).ToListAsync(ct);
        var accountId = PostingService.Resolve(rules, accountRole, null);
        return await (from l in db.Set<JournalLine>()
                      join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                      where l.AccountId == accountId && l.PartyType == partyType && l.PartyId == partyId && e.PostingDate <= asOf
                      select l.DebitBase - l.CreditBase).SumAsync(ct);
    }

    public async Task<PartyLedger> GetPartyLedgerAsync(string accountRole, PartyType partyType, Guid partyId, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken ct = default)
    {
        var opening = await GetPartyBalanceAsync(accountRole, partyType, partyId, periodStart.AddDays(-1), ct);
        var companyId = tenant.RequireCompanyId();
        var rules = await db.Set<PostingRule>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.AccountRole == accountRole && r.RoleKey == null).ToListAsync(ct);
        var accountId = PostingService.Resolve(rules, accountRole, null);
        var lines = await (from l in db.Set<JournalLine>().AsNoTracking()
                           join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                           where l.AccountId == accountId && l.PartyType == partyType && l.PartyId == partyId
                                 && e.PostingDate >= periodStart && e.PostingDate <= periodEnd
                           orderby e.PostingDate, e.EntryNo, l.LineNo
                           select new PartyLedgerLine(e.PostingDate, e.EntryNo, e.SourceType, e.SourceId, e.SourceNo, e.MemoEn, e.MemoAr,
                               l.DebitBase, l.CreditBase)).ToListAsync(ct);
        return new PartyLedger(opening, lines, opening + lines.Sum(l => l.Debit - l.Credit));
    }
}

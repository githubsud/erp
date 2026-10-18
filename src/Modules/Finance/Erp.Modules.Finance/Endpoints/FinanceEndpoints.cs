using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Banking;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Finance.Posting;
using Erp.Modules.Finance.Provisioning;
using Erp.Modules.Finance.Reports;
using Erp.Modules.Finance.Vouchers;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Endpoints;

public sealed record AccountDto(Guid Id, Guid? ParentId, string Code, string NameEn, string NameAr, string AccountType,
    string NormalBalance, bool IsControl, string? ControlSubledger, string? Subtype, bool IsPostable, bool IsActive);
public sealed record CreateAccountRequest(Guid ParentId, string Code, string? NameEn, string? NameAr, bool IsPostable = true, string? Subtype = null);
public sealed record UpdateAccountRequest(string? NameEn, string? NameAr, bool IsActive, string? Subtype);
public sealed record CostCenterDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive);
public sealed record CreateCostCenterRequest(string Code, string? NameEn, string? NameAr);
public sealed record FiscalPeriodDto(Guid Id, int PeriodNo, DateOnly StartDate, DateOnly EndDate, string Status);
public sealed record FiscalYearDto(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status, IReadOnlyList<FiscalPeriodDto> Periods);
public sealed record CreateFiscalYearRequest(DateOnly StartDate);
public sealed record PostingRuleDto(Guid Id, string AccountRole, Guid? RoleKey, Guid AccountId, string AccountCode);
public sealed record SetPostingRuleRequest(string AccountRole, Guid? RoleKey, Guid AccountId);
public sealed record JournalEntrySummary(Guid Id, string EntryNo, DateOnly PostingDate, string SourceType, Guid SourceId, string SourceNo,
    Guid? ReversalOfId, string? MemoEn, string? MemoAr, decimal TotalDebit);
public sealed record JournalLineDto(int LineNo, Guid AccountId, string AccountCode, string AccountNameEn, string AccountNameAr,
    string? PartyType, Guid? PartyId, Guid? CostCenterId, string Currency, decimal FxRate, decimal DebitTxn, decimal CreditTxn,
    decimal DebitBase, decimal CreditBase, string? Description);
public sealed record JournalEntryDetail(JournalEntrySummary Entry, IReadOnlyList<JournalLineDto> Lines);

internal static class FinanceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization();

        g.MapGet("/accounts", ListAccounts).RequirePermission(FinancePermissions.AccountsRead);
        g.MapPost("/accounts", CreateAccount).RequirePermission(FinancePermissions.AccountsManage);
        g.MapPut("/accounts/{id:guid}", UpdateAccount).RequirePermission(FinancePermissions.AccountsManage);

        g.MapGet("/cost-centers", ListCostCenters).RequirePermission(FinancePermissions.AccountsRead);
        g.MapPost("/cost-centers", CreateCostCenter).RequirePermission(FinancePermissions.AccountsManage);

        g.MapGet("/fiscal-years", ListFiscalYears).RequirePermission(FinancePermissions.AccountsRead);
        g.MapPost("/fiscal-years", CreateFiscalYear).RequirePermission(FinancePermissions.PeriodsManage);
        g.MapPost("/periods/{id:guid}/close", (Guid id, ErpDbContext db, CancellationToken ct) => SetPeriodStatus(id, FiscalStatus.Closed, db, ct))
            .RequirePermission(FinancePermissions.PeriodsManage);
        g.MapPost("/periods/{id:guid}/reopen", (Guid id, ErpDbContext db, CancellationToken ct) => SetPeriodStatus(id, FiscalStatus.Open, db, ct))
            .RequirePermission(FinancePermissions.PeriodsManage);

        g.MapGet("/posting-rules", ListPostingRules).RequirePermission(FinancePermissions.AccountsRead);
        g.MapPut("/posting-rules", SetPostingRule).RequirePermission(FinancePermissions.RulesManage);

        g.MapGet("/journal-vouchers", ListVouchers).RequirePermission(FinancePermissions.JournalRead);
        g.MapPost("/journal-vouchers", (CreateJournalVoucherRequest r, JournalVoucherService s, CancellationToken ct) => s.CreateAndPostAsync(r, ct))
            .RequirePermission(FinancePermissions.JournalPost);
        g.MapPost("/journal-vouchers/{id:guid}/reverse", (Guid id, ReverseRequest r, JournalVoucherService s, CancellationToken ct) => s.ReverseAsync(id, r, ct))
            .RequirePermission(FinancePermissions.JournalReverse);

        g.MapGet("/journal-entries", ListEntries).RequirePermission(FinancePermissions.JournalRead);
        g.MapGet("/journal-entries/{id:guid}", GetEntry).RequirePermission(FinancePermissions.JournalRead);

        g.MapGet("/reports/trial-balance", (DateOnly from, DateOnly to, FinanceReports r, CancellationToken ct) => r.TrialBalanceAsync(from, to, ct))
            .RequirePermission(FinancePermissions.ReportsRead);
        g.MapGet("/reports/account-ledger", (Guid accountId, DateOnly from, DateOnly to, FinanceReports r, CancellationToken ct) =>
            r.AccountLedgerAsync(accountId, from, to, ct)).RequirePermission(FinancePermissions.ReportsRead);
        g.MapGet("/reports/profit-and-loss", (DateOnly from, DateOnly to, FinancialStatements s, CancellationToken ct, DateOnly? compareFrom = null,
            DateOnly? compareTo = null) => s.ProfitAndLossAsync(from, to, compareFrom, compareTo, ct)).RequirePermission(FinancePermissions.ReportsRead);
        g.MapGet("/reports/profit-and-loss.pdf", async (DateOnly from, DateOnly to, StatementsPdf pdf, CancellationToken ct, DateOnly? compareFrom = null,
            DateOnly? compareTo = null) =>
        {
            var (content, name) = await pdf.ProfitAndLossAsync(from, to, compareFrom, compareTo, ct);
            return Results.File(content, "application/pdf", name);
        }).RequirePermission(FinancePermissions.ReportsRead);
        g.MapGet("/reports/balance-sheet", (DateOnly asOf, FinancialStatements s, CancellationToken ct, DateOnly? compareAsOf = null) =>
            s.BalanceSheetAsync(asOf, compareAsOf, ct)).RequirePermission(FinancePermissions.ReportsRead);
        g.MapGet("/reports/balance-sheet.pdf", async (DateOnly asOf, StatementsPdf pdf, CancellationToken ct, DateOnly? compareAsOf = null) =>
        {
            var (content, name) = await pdf.BalanceSheetAsync(asOf, compareAsOf, ct);
            return Results.File(content, "application/pdf", name);
        }).RequirePermission(FinancePermissions.ReportsRead);
        BankEndpoints.Map(g);
        g.MapGet("/exports/journal.csv", async (DateOnly from, DateOnly to, FinanceReports r, CancellationToken ct) =>
                Results.File(await r.ExportJournalCsvAsync(from, to, ct), "text/csv; charset=utf-8", $"journal_{from:yyyyMMdd}_{to:yyyyMMdd}.csv"))
            .RequirePermission(FinancePermissions.ReportsRead);
    }

    private static async Task<List<AccountDto>> ListAccounts(ErpDbContext db, ITenantContext tenant, CancellationToken ct, bool includeInactive = false) =>
        (await db.Set<Account>().AsNoTracking()
            .Where(a => a.CompanyId == tenant.RequireCompanyId() && (includeInactive || a.IsActive))
            .ToListAsync(ct))
        .OrderBy(a => a.Code, StringComparer.Ordinal)
        .Select(ToDto).ToList();

    private static async Task<AccountDto> CreateAccount(CreateAccountRequest request, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var parent = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.ParentId && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Parent account", request.ParentId);
        if (parent.IsPostable)
            throw new BusinessRuleException("fin.parent_postable", "Accounts can only be created under a group account.", "يمكن إنشاء الحسابات تحت حساب تجميعي فقط.");
        var code = Guard.Required(request.Code, "code");
        if (!code.StartsWith(parent.Code, StringComparison.Ordinal))
            throw new BusinessRuleException("fin.code_prefix", $"The account code must start with the parent code {parent.Code}.", $"يجب أن يبدأ رمز الحساب برمز الحساب الأب {parent.Code}.");
        var (en, ar) = Bilingual.Require(request.NameEn, request.NameAr, "account name");

        var account = new Account
        {
            CompanyId = companyId,
            ParentId = parent.Id,
            Code = code,
            NameEn = en,
            NameAr = ar,
            AccountType = parent.AccountType,
            NormalBalance = ChartOfAccountsTemplate.DefaultNormal(parent.AccountType),
            IsPostable = request.IsPostable,
            Subtype = request.Subtype
        };
        db.Add(account);
        await db.SaveChangesAsync(ct);
        return ToDto(account);
    }

    private static async Task<AccountDto> UpdateAccount(Guid id, UpdateAccountRequest request, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var account = await db.Set<Account>().SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Account", id);
        (account.NameEn, account.NameAr) = Bilingual.Require(request.NameEn, request.NameAr, "account name");
        account.IsActive = request.IsActive;
        account.Subtype = request.Subtype;
        await db.SaveChangesAsync(ct);
        return ToDto(account);
    }

    private static AccountDto ToDto(Account a) => new(a.Id, a.ParentId, a.Code, a.NameEn, a.NameAr, a.AccountType.ToString(),
        a.NormalBalance.ToString(), a.IsControl, a.ControlSubledger?.ToString(), a.Subtype, a.IsPostable, a.IsActive);

    private static async Task<List<CostCenterDto>> ListCostCenters(ErpDbContext db, ITenantContext tenant, CancellationToken ct) =>
        await db.Set<CostCenter>().AsNoTracking().Where(c => c.CompanyId == tenant.RequireCompanyId()).OrderBy(c => c.Code)
            .Select(c => new CostCenterDto(c.Id, c.Code, c.NameEn, c.NameAr, c.IsActive)).ToListAsync(ct);

    private static async Task<CostCenterDto> CreateCostCenter(CreateCostCenterRequest request, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(request.NameEn, request.NameAr);
        var c = new CostCenter { CompanyId = tenant.RequireCompanyId(), Code = Guard.Required(request.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar };
        db.Add(c);
        await db.SaveChangesAsync(ct);
        return new CostCenterDto(c.Id, c.Code, c.NameEn, c.NameAr, c.IsActive);
    }

    private static async Task<List<FiscalYearDto>> ListFiscalYears(ErpDbContext db, ITenantContext tenant, CancellationToken ct) =>
        (await db.Set<FiscalYear>().AsNoTracking().Include(y => y.Periods)
            .Where(y => y.CompanyId == tenant.RequireCompanyId()).OrderBy(y => y.StartDate).ToListAsync(ct))
        .Select(ToDto).ToList();

    private static async Task<FiscalYearDto> CreateFiscalYear(CreateFiscalYearRequest request, FiscalCalendar calendar, ErpDbContext db, CancellationToken ct)
    {
        var year = await calendar.CreateYearAsync(request.StartDate, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(year);
    }

    private static FiscalYearDto ToDto(FiscalYear y) => new(y.Id, y.Name, y.StartDate, y.EndDate, y.Status.ToString(),
        y.Periods.OrderBy(p => p.PeriodNo).Select(p => new FiscalPeriodDto(p.Id, p.PeriodNo, p.StartDate, p.EndDate, p.Status.ToString())).ToList());

    private static async Task<IResult> SetPeriodStatus(Guid id, FiscalStatus status, ErpDbContext db, CancellationToken ct)
    {
        var period = await db.Set<FiscalPeriod>().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Fiscal period", id);
        period.Status = status;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<List<PostingRuleDto>> ListPostingRules(ErpDbContext db, ITenantContext tenant, CancellationToken ct) =>
        await (from r in db.Set<PostingRule>()
               join a in db.Set<Account>() on r.AccountId equals a.Id
               where r.CompanyId == tenant.RequireCompanyId()
               orderby r.AccountRole
               select new PostingRuleDto(r.Id, r.AccountRole, r.RoleKey, r.AccountId, a.Code)).ToListAsync(ct);

    private static async Task<IResult> SetPostingRule(SetPostingRuleRequest request, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var account = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.AccountId && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Account", request.AccountId);
        if (!account.IsPostable)
            throw new BusinessRuleException("fin.account_not_postable", "Posting rules must point to a postable account.", "يجب أن تشير قواعد الترحيل إلى حساب يقبل الترحيل.");
        var rule = await db.Set<PostingRule>().SingleOrDefaultAsync(r => r.CompanyId == companyId && r.AccountRole == request.AccountRole && r.RoleKey == request.RoleKey, ct);
        if (rule is null)
            db.Add(new PostingRule { CompanyId = companyId, AccountRole = Guard.Required(request.AccountRole, "accountRole"), RoleKey = request.RoleKey, AccountId = account.Id });
        else
            rule.AccountId = account.Id;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<List<JournalVoucherDto>> ListVouchers(ErpDbContext db, ITenantContext tenant, CancellationToken ct) =>
        (await db.Set<JournalVoucher>().AsNoTracking().Where(v => v.CompanyId == tenant.RequireCompanyId())
            .OrderByDescending(v => v.VoucherDate).ThenByDescending(v => v.VoucherNo).Take(500).ToListAsync(ct))
        .Select(JournalVoucherService.ToDto).ToList();

    private static async Task<PagedResult<JournalEntrySummary>> ListEntries(ErpDbContext db, ITenantContext tenant, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? sourceType = null, Guid? sourceId = null, int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.Set<JournalEntry>().AsNoTracking().Where(e => e.CompanyId == tenant.RequireCompanyId());
        if (from is not null) q = q.Where(e => e.PostingDate >= from);
        if (to is not null) q = q.Where(e => e.PostingDate <= to);
        if (!string.IsNullOrWhiteSpace(sourceType)) q = q.Where(e => e.SourceType == sourceType);
        if (sourceId is not null) q = q.Where(e => e.SourceId == sourceId);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.PostingDate).ThenByDescending(e => e.EntryNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new JournalEntrySummary(e.Id, e.EntryNo, e.PostingDate, e.SourceType, e.SourceId, e.SourceNo, e.ReversalOfId,
                e.MemoEn, e.MemoAr, e.Lines.Sum(l => l.DebitBase)))
            .ToListAsync(ct);
        return new PagedResult<JournalEntrySummary>(items, total, page, pageSize);
    }

    private static async Task<JournalEntryDetail> GetEntry(Guid id, ErpDbContext db, CancellationToken ct)
    {
        var e = await db.Set<JournalEntry>().AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Journal entry", id);
        var accountIds = e.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var summary = new JournalEntrySummary(e.Id, e.EntryNo, e.PostingDate, e.SourceType, e.SourceId, e.SourceNo, e.ReversalOfId,
            e.MemoEn, e.MemoAr, e.Lines.Sum(l => l.DebitBase));
        return new JournalEntryDetail(summary, e.Lines.OrderBy(l => l.LineNo).Select(l => new JournalLineDto(l.LineNo, l.AccountId,
            accounts[l.AccountId].Code, accounts[l.AccountId].NameEn, accounts[l.AccountId].NameAr, l.PartyType?.ToString(), l.PartyId,
            l.CostCenterId, l.Currency, l.FxRate, l.DebitTxn, l.CreditTxn, l.DebitBase, l.CreditBase, l.Description)).ToList());
    }
}

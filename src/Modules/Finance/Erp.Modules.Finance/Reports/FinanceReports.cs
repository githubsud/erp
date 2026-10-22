using System.Globalization;
using System.Text;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Reports;

public sealed record TrialBalanceRow(Guid AccountId, string Code, string NameEn, string NameAr, string AccountType,
    decimal Opening, decimal Debit, decimal Credit, decimal Closing);

public sealed record TrialBalance(DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceRow> Rows,
    decimal TotalOpening, decimal TotalDebit, decimal TotalCredit, decimal TotalClosing, bool IsBalanced);

public sealed record LedgerRow(DateOnly PostingDate, string EntryNo, string SourceType, string SourceNo, string? Description,
    decimal Debit, decimal Credit, decimal Balance, Guid JournalEntryId);

public sealed record AccountLedger(Guid AccountId, string Code, string NameEn, string NameAr, DateOnly From, DateOnly To,
    decimal Opening, IReadOnlyList<LedgerRow> Rows, decimal Closing);

internal sealed class FinanceReports(ErpDbContext db, ITenantContext tenant)
{
    /// <summary>
    /// Opening balances respect the fiscal year: income and expense accounts start from zero at the start of the
    /// fiscal year containing <paramref name="from"/>, and prior years' results roll into retained earnings.
    /// </summary>
    public async Task<TrialBalance> TrialBalanceAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) throw new BusinessRuleException("report.invalid_range", "'to' must be on or after 'from'.", "يجب أن يكون تاريخ النهاية بعد تاريخ البداية.");
        var tenantId = tenant.RequireTenantId();
        var companyId = tenant.RequireCompanyId();
        var fiscalYearStart = await db.Set<FiscalYear>().AsNoTracking()
            .Where(y => y.CompanyId == companyId && y.StartDate <= from && y.EndDate >= from)
            .Select(y => (DateOnly?)y.StartDate).SingleOrDefaultAsync(ct) ?? new DateOnly(from.Year, 1, 1);

        // Raw SQL bypasses EF query filters, so the tenant predicate is explicit (RLS enforces it again).
        // Column aliases are snake_case because the naming convention also maps raw-query result types.
        var sums = await db.Database.SqlQuery<AccountSums>($"""
            SELECT l.account_id AS account_id,
                   COALESCE(SUM(l.debit_base - l.credit_base) FILTER (WHERE e.posting_date < {fiscalYearStart}), 0) AS before_year,
                   COALESCE(SUM(l.debit_base - l.credit_base) FILTER (WHERE e.posting_date >= {fiscalYearStart} AND e.posting_date < {from}), 0) AS year_to_from,
                   COALESCE(SUM(l.debit_base)  FILTER (WHERE e.posting_date >= {from}), 0) AS debit,
                   COALESCE(SUM(l.credit_base) FILTER (WHERE e.posting_date >= {from}), 0) AS credit
              FROM fin.journal_line l
              JOIN fin.journal_entry e ON e.tenant_id = l.tenant_id AND e.id = l.journal_entry_id
             WHERE l.tenant_id = {tenantId} AND e.company_id = {companyId} AND e.posting_date <= {to}
             GROUP BY l.account_id
            """).ToListAsync(ct);

        var accounts = await db.Set<Account>().AsNoTracking().Where(a => a.CompanyId == companyId).ToDictionaryAsync(a => a.Id, ct);
        var priorYearsResult = sums.Where(s => accounts[s.AccountId].IsProfitAndLoss).Sum(s => s.BeforeYear);
        var retainedEarnings = accounts.Values.FirstOrDefault(a => a.Subtype == "retained_earnings");

        var rows = new List<TrialBalanceRow>();
        foreach (var s in sums)
        {
            var account = accounts[s.AccountId];
            var opening = account.IsProfitAndLoss ? s.YearToFrom : s.BeforeYear + s.YearToFrom;
            if (account.Id == retainedEarnings?.Id) opening += priorYearsResult;
            rows.Add(Row(account, opening, s.Debit, s.Credit));
        }
        if (retainedEarnings is not null && priorYearsResult != 0 && rows.All(r => r.AccountId != retainedEarnings.Id))
            rows.Add(Row(retainedEarnings, priorYearsResult, 0, 0));

        rows = rows.Where(r => r.Opening != 0 || r.Debit != 0 || r.Credit != 0).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        var totalDebit = rows.Sum(r => r.Debit);
        var totalCredit = rows.Sum(r => r.Credit);
        var totalOpening = rows.Sum(r => r.Opening);
        var totalClosing = rows.Sum(r => r.Closing);
        return new TrialBalance(from, to, rows, totalOpening, totalDebit, totalCredit, totalClosing,
            totalDebit == totalCredit && totalOpening == 0 && totalClosing == 0);
    }

    public async Task<AccountLedger> AccountLedgerAsync(Guid accountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var account = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Account", accountId);

        var lines = from l in db.Set<JournalLine>()
                    join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                    where l.AccountId == accountId && e.CompanyId == companyId
                    select new { e.PostingDate, e.EntryNo, e.SourceType, e.SourceNo, Description = l.Description ?? e.MemoEn, l.DebitBase, l.CreditBase, e.Id, l.LineNo };

        var opening = await lines.Where(x => x.PostingDate < from).SumAsync(x => x.DebitBase - x.CreditBase, ct);
        var movements = await lines.Where(x => x.PostingDate >= from && x.PostingDate <= to)
            .OrderBy(x => x.PostingDate).ThenBy(x => x.EntryNo).ThenBy(x => x.LineNo).ToListAsync(ct);

        var balance = opening;
        var rows = movements.Select(m =>
        {
            balance += m.DebitBase - m.CreditBase;
            return new LedgerRow(m.PostingDate, m.EntryNo, m.SourceType, m.SourceNo, m.Description, m.DebitBase, m.CreditBase, balance, m.Id);
        }).ToList();
        return new AccountLedger(account.Id, account.Code, account.NameEn, account.NameAr, from, to, opening, rows, balance);
    }

    /// <summary>
    /// Journal export in a flat CSV that Excel opens correctly (UTF-8 with BOM, Arabic preserved) and that maps to
    /// Tally/QuickBooks journal imports, so payroll-only customers can keep their existing books.
    /// </summary>
    public async Task<byte[]> ExportJournalCsvAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var (start, end) = (from, to);
        var rows = await (from l in db.Set<JournalLine>()
                          join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                          join a in db.Set<Account>() on l.AccountId equals a.Id
                          join c in db.Set<CostCenter>() on l.CostCenterId equals c.Id into cc
                          from c in cc.DefaultIfEmpty()
                          where e.CompanyId == companyId && e.PostingDate >= start && e.PostingDate <= end
                          orderby e.PostingDate, e.EntryNo, l.LineNo
                          select new
                          {
                              e.PostingDate, e.EntryNo, e.SourceType, e.SourceNo, a.Code, a.NameEn, a.NameAr,
                              l.DebitBase, l.CreditBase, CostCenter = c == null ? null : c.Code, Narration = l.Description ?? e.MemoEn
                          }).ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Date,Entry No,Source Type,Source No,Account Code,Account Name,Account Name (Arabic),Debit,Credit,Cost Center,Narration");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',',
                r.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Csv(r.EntryNo), Csv(r.SourceType), Csv(r.SourceNo),
                Csv(r.Code), Csv(r.NameEn), Csv(r.NameAr),
                r.DebitBase.ToString("0.00", CultureInfo.InvariantCulture), r.CreditBase.ToString("0.00", CultureInfo.InvariantCulture),
                Csv(r.CostCenter), Csv(r.Narration)));

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Csv(string? value) => value is null ? "" : CsvFile.Escape(value);

    private static TrialBalanceRow Row(Account a, decimal opening, decimal debit, decimal credit) =>
        new(a.Id, a.Code, a.NameEn, a.NameAr, a.AccountType.ToString(), opening, debit, credit, opening + debit - credit);

    private sealed record AccountSums(Guid AccountId, decimal BeforeYear, decimal YearToFrom, decimal Debit, decimal Credit);
}

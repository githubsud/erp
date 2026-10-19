using Erp.SharedKernel.Application;

namespace Erp.Modules.Finance.Reports;

/// <summary>Month-to-date trial balance.</summary>
internal sealed class TrialBalanceScheduledReport(FinanceReports reports, IClock clock) : IScheduledReport
{
    public string Key => "fin.trial_balance";
    public string NameEn => "Trial balance (month to date)";
    public string NameAr => "ميزان المراجعة (من بداية الشهر)";
    public string Permission => FinancePermissions.ReportsRead;

    public async Task<ReportFile> RenderAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var tb = await reports.TrialBalanceAsync(new DateOnly(today.Year, today.Month, 1), today, ct);
        var content = CsvFile.Build(["Code", "Account", "الحساب", "Opening", "Debit", "Credit", "Closing"],
            tb.Rows.Select(r => (IReadOnlyList<object?>)[r.Code, r.NameEn, r.NameAr, r.Opening, r.Debit, r.Credit, r.Closing]));
        return new ReportFile($"trial_balance_{today:yyyyMMdd}.csv", CsvFile.ContentType, content,
            $"{tb.From:dd/MM/yyyy} – {tb.To:dd/MM/yyyy}: debits {tb.TotalDebit:N2}, credits {tb.TotalCredit:N2}{(tb.IsBalanced ? "" : " — NOT BALANCED")}.",
            $"{tb.From:dd/MM/yyyy} – {tb.To:dd/MM/yyyy}: مدين {tb.TotalDebit:N2}، دائن {tb.TotalCredit:N2}{(tb.IsBalanced ? "" : " — غير متوازن")}.");
    }
}

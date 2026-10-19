using ClosedXML.Excel;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Endpoints;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.HR.Reports;

public sealed class GrsiaPayment : TenantEntity
{
    public Guid CompanyId { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public decimal Amount { get; init; }
    public DateOnly PaymentDate { get; init; }
    public Guid AccountId { get; init; }
    public string? Reference { get; init; }
    public Guid JournalEntryId { get; set; }
}

internal sealed class GrsiaPaymentConfiguration : IEntityTypeConfiguration<GrsiaPayment>
{
    public void Configure(EntityTypeBuilder<GrsiaPayment> b) => b.ToTable("grsia_payment", "hr");
}

public sealed record GrsiaRow(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, string? Qid, string? RunNo, decimal PaidDays,
    decimal ContributionSalary, decimal EmployeeRatePercent, decimal EmployeeShare, decimal EmployerShare, decimal Total);

public sealed record GrsiaSchedule(int Year, int Month, IReadOnlyList<GrsiaRow> Rows, decimal ContributionSalary, decimal EmployeeShare, decimal EmployerShare,
    decimal Total, bool AllPosted, GrsiaPaymentDto? Payment, decimal LedgerBalance);

public sealed record GrsiaPaymentDto(Guid Id, decimal Amount, DateOnly PaymentDate, Guid AccountId, string? Reference, Guid JournalEntryId);

public sealed record GrsiaMonth(int Month, int Employees, decimal EmployeeShare, decimal EmployerShare, decimal Total, DateOnly? PaidOn, decimal? PaidAmount);

public sealed record PayGrsiaRequest(int Year, int Month, Guid AccountId, DateOnly PaymentDate, string? Reference);

/// <summary>
/// GRSIA (social insurance for Qatari employees): the monthly contribution schedule built from approved payslips, and the payment
/// that clears social insurance payable. ⚠️ VERIFY the GRSIA upload format before using the export for filing.
/// </summary>
internal sealed class GrsiaService(ErpDbContext db, ITenantContext tenant, IPostingService posting, IFinanceQueries finance)
{
    public const string SourceType = "hr_grsia_payment";
    private static readonly PayrollStatus[] Final = [PayrollStatus.Approved, PayrollStatus.Posted, PayrollStatus.Paid];

    public async Task<GrsiaSchedule> ScheduleAsync(int year, int month, bool showSensitive, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var runs = await db.Set<PayrollRun>().AsNoTracking().Where(r => r.CompanyId == companyId && r.Year == year && r.Month == month && Final.Contains(r.Status))
            .ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var payslips = await db.Set<Payslip>().AsNoTracking().Include(p => p.Lines).Where(p => runIds.Contains(p.PayrollRunId)).OrderBy(p => p.EmpNo).ToListAsync(ct);
        var rows = new List<GrsiaRow>();
        foreach (var p in payslips)
        {
            var line = p.Lines.FirstOrDefault(l => l.Code == "SI_EMPLOYEE");
            if (line is null && p.EmployerSocialInsurance == 0) continue;
            var employeeShare = line?.Amount ?? 0;
            rows.Add(new GrsiaRow(p.EmployeeId, p.EmpNo, p.NameEn, p.NameAr, HrEndpoints.Mask(p.Qid, showSensitive), runs.Single(r => r.Id == p.PayrollRunId).RunNo,
                p.PaidDays, line?.Quantity ?? 0, line?.Rate ?? 0, employeeShare, p.EmployerSocialInsurance, employeeShare + p.EmployerSocialInsurance));
        }
        var payment = await db.Set<GrsiaPayment>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Year == year && x.Month == month, ct);
        var monthEnd = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        var ledger = -await finance.GetRoleBalanceAsync(AccountRoles.SocialInsurancePayable, monthEnd, ct);
        return new GrsiaSchedule(year, month, rows, rows.Sum(r => r.ContributionSalary), rows.Sum(r => r.EmployeeShare), rows.Sum(r => r.EmployerShare),
            rows.Sum(r => r.Total), runs.Count > 0 && runs.All(r => r.Status is PayrollStatus.Posted or PayrollStatus.Paid),
            payment is null ? null : new GrsiaPaymentDto(payment.Id, payment.Amount, payment.PaymentDate, payment.AccountId, payment.Reference, payment.JournalEntryId),
            ledger);
    }

    public async Task<List<GrsiaMonth>> YearAsync(int year, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var lines = await (from p in db.Set<Payslip>().AsNoTracking()
                           join r in db.Set<PayrollRun>() on p.PayrollRunId equals r.Id
                           where r.CompanyId == companyId && r.Year == year && Final.Contains(r.Status)
                           select new
                           {
                               r.Month, p.EmployeeId, p.EmployerSocialInsurance,
                               EmployeeShare = p.Lines.Where(l => l.Code == "SI_EMPLOYEE").Sum(l => (decimal?)l.Amount) ?? 0
                           }).ToListAsync(ct);
        var payments = await db.Set<GrsiaPayment>().AsNoTracking().Where(x => x.CompanyId == companyId && x.Year == year).ToDictionaryAsync(x => x.Month, ct);
        return Enumerable.Range(1, 12).Select(m =>
        {
            var month = lines.Where(l => l.Month == m && (l.EmployeeShare > 0 || l.EmployerSocialInsurance > 0)).ToList();
            var paid = payments.GetValueOrDefault(m);
            return new GrsiaMonth(m, month.Select(l => l.EmployeeId).Distinct().Count(), month.Sum(l => l.EmployeeShare), month.Sum(l => l.EmployerSocialInsurance),
                month.Sum(l => l.EmployeeShare + l.EmployerSocialInsurance), paid?.PaymentDate, paid?.Amount);
        }).ToList();
    }

    /// <summary>Pays the month's contributions: Dr social insurance payable, Cr bank — once per month, after payroll is posted.</summary>
    public Task<GrsiaPayment> PayAsync(PayGrsiaRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            if (await db.Set<GrsiaPayment>().AnyAsync(x => x.CompanyId == companyId && x.Year == r.Year && x.Month == r.Month, token))
                throw new ConflictException("hr.grsia_paid", "This month's contributions are already paid.", "تم سداد اشتراكات هذا الشهر مسبقاً.");
            var schedule = await ScheduleAsync(r.Year, r.Month, false, token);
            if (!schedule.AllPosted || schedule.Total <= 0)
                throw new BusinessRuleException("hr.grsia_not_posted", "Post the month's payroll first; there is nothing to pay yet.",
                    "قم بترحيل رواتب الشهر أولاً؛ لا يوجد ما يُسدد بعد.");
            var account = await finance.FindPostableAccountAsync(r.AccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("hr.payment_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");
            var payment = new GrsiaPayment
            {
                CompanyId = companyId, Year = r.Year, Month = r.Month, Amount = schedule.Total, PaymentDate = r.PaymentDate, AccountId = account.Id,
                Reference = string.IsNullOrWhiteSpace(r.Reference) ? null : r.Reference.Trim()
            };
            var entry = await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = payment.Id, SourceNo = $"GRSIA-{r.Year}-{r.Month:D2}", PostingDate = r.PaymentDate,
                MemoEn = $"GRSIA contributions {r.Year}-{r.Month:D2}", MemoAr = $"اشتراكات التأمينات الاجتماعية {r.Year}-{r.Month:D2}",
                Lines =
                [
                    new PostingLine { AccountRole = AccountRoles.SocialInsurancePayable, Side = PostingSide.Debit, Amount = schedule.Total },
                    new PostingLine { AccountId = account.Id, Side = PostingSide.Credit, Amount = schedule.Total }
                ]
            }, token);
            payment.JournalEntryId = entry.JournalEntryId;
            db.Add(payment);
            await db.SaveChangesAsync(token);
            return payment;
        }, ct);

    public async Task<(byte[] Content, string FileName)> ExcelAsync(int year, int month, bool showSensitive, CancellationToken ct)
    {
        var s = await ScheduleAsync(year, month, showSensitive, ct);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add($"GRSIA {year}-{month:D2}");
        string[] headers = ["Emp No", "Name", "الاسم", "QID", "Paid days", "Contribution salary", "Employee rate %", "Employee share", "Employer share", "Total"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var x in s.Rows)
        {
            sheet.Cell(row, 1).Value = x.EmpNo;
            sheet.Cell(row, 2).Value = x.NameEn;
            sheet.Cell(row, 3).Value = x.NameAr;
            sheet.Cell(row, 4).Value = x.Qid ?? "";
            sheet.Cell(row, 5).Value = x.PaidDays;
            sheet.Cell(row, 6).Value = x.ContributionSalary;
            sheet.Cell(row, 7).Value = x.EmployeeRatePercent;
            sheet.Cell(row, 8).Value = x.EmployeeShare;
            sheet.Cell(row, 9).Value = x.EmployerShare;
            sheet.Cell(row, 10).Value = x.Total;
            row++;
        }
        sheet.Cell(row, 1).Value = "Total / الإجمالي";
        sheet.Cell(row, 6).Value = s.ContributionSalary;
        sheet.Cell(row, 8).Value = s.EmployeeShare;
        sheet.Cell(row, 9).Value = s.EmployerShare;
        sheet.Cell(row, 10).Value = s.Total;
        sheet.Row(row).Style.Font.Bold = true;
        sheet.Range(2, 6, row, 10).Style.NumberFormat.Format = "#,##0.00";
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return (stream.ToArray(), $"grsia_{year}_{month:D2}.xlsx");
    }
}

internal static class GrsiaEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hr/reports/grsia").WithTags("HR & Payroll").RequireAuthorization();
        g.MapGet("", async (int year, int month, GrsiaService s, IPermissionService p, CancellationToken ct) =>
            await s.ScheduleAsync(year, month, await p.HasAsync(HrPermissions.SensitiveView, ct), ct)).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/year", (int year, GrsiaService s, CancellationToken ct) => s.YearAsync(year, ct)).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/xlsx", async (int year, int month, GrsiaService s, IPermissionService p, CancellationToken ct) =>
        {
            var (content, name) = await s.ExcelAsync(year, month, await p.HasAsync(HrPermissions.SensitiveView, ct), ct);
            return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }).RequirePermission(HrPermissions.PayrollRead);
        g.MapPost("/pay", async (PayGrsiaRequest r, GrsiaService s, CancellationToken ct) =>
        {
            var x = await s.PayAsync(r, ct);
            return new GrsiaPaymentDto(x.Id, x.Amount, x.PaymentDate, x.AccountId, x.Reference, x.JournalEntryId);
        }).RequirePermission(HrPermissions.PayrollPost);
    }
}

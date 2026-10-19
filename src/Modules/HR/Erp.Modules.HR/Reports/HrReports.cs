using ClosedXML.Excel;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.People;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Reports;

public sealed record RegisterColumn(string Code, string NameEn, string NameAr, string Kind);

public sealed record RegisterRow(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, string? DepartmentEn, string? DepartmentAr, bool InWps,
    decimal PaidDays, IReadOnlyDictionary<string, decimal> Amounts, decimal Gross, decimal Deductions, decimal Net);

public sealed record PayrollRegister(Guid RunId, string RunNo, int Year, int Month, string Status, IReadOnlyList<RegisterColumn> Columns,
    IReadOnlyList<RegisterRow> Rows, IReadOnlyDictionary<string, decimal> Totals, decimal Gross, decimal Deductions, decimal Net);

public sealed record CountRow(string Key, string LabelEn, string LabelAr, int Count, int Male, int Female);

public sealed record Headcount(DateOnly AsOf, int Total, int Qatari, decimal QatariPercent, int JoinedThisYear, int LeftThisYear,
    IReadOnlyList<CountRow> ByNationality, IReadOnlyList<CountRow> ByDepartment);

public sealed record LeaveLiabilityRow(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, decimal BalanceDays, decimal DailyRate, decimal Liability,
    decimal Provision);

public sealed record LeaveLiability(DateOnly AsOf, IReadOnlyList<LeaveLiabilityRow> Rows, decimal TotalLiability, decimal TotalProvision, decimal LedgerBalance);

public sealed record WpsReconciliationRow(Guid RunId, string RunNo, int Year, int Month, string Status, DateOnly WpsDeadline, decimal PayrollNet,
    decimal WpsNet, int WpsEmployees, decimal OutsideWpsNet, int OutsideWpsEmployees, decimal? SifTotal, int? SifRecords, string? SifStatus,
    DateOnly? PaymentDate, bool Matches, IReadOnlyList<string> Issues);

/// <summary>Payroll register, headcount, leave liability and WPS reconciliation.</summary>
internal sealed class HrReportService(ErpDbContext db, ITenantContext tenant, LeaveService leave, IFinanceQueries finance, IClock clock)
{
    public async Task<PayrollRegister> RegisterAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.CompanyId == tenant.RequireCompanyId(), ct)
                  ?? throw new NotFoundException("Payroll run", runId);
        var payslips = await db.Set<Payslip>().AsNoTracking().Include(p => p.Lines).Where(p => p.PayrollRunId == runId).OrderBy(p => p.EmpNo).ToListAsync(ct);
        var employeeIds = payslips.Select(p => p.EmployeeId).ToList();
        var departments = await (from e in db.Set<Employee>().AsNoTracking()
                                 join d in db.Set<Department>() on e.DepartmentId equals d.Id
                                 where employeeIds.Contains(e.Id)
                                 select new { e.Id, d.NameEn, d.NameAr }).ToDictionaryAsync(x => x.Id, ct);
        var columns = payslips.SelectMany(p => p.Lines)
            .GroupBy(l => l.Code)
            .Select(g => g.First())
            .OrderBy(l => l.Kind).ThenBy(l => l.LineNo).ThenBy(l => l.Code)
            .Select(l => new RegisterColumn(l.Code, l.NameEn, l.NameAr, l.Kind.ToString())).ToList();
        var rows = payslips.Select(p =>
        {
            var dept = departments.GetValueOrDefault(p.EmployeeId);
            return new RegisterRow(p.EmployeeId, p.EmpNo, p.NameEn, p.NameAr, dept?.NameEn, dept?.NameAr, p.InWps, p.PaidDays,
                p.Lines.GroupBy(l => l.Code).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount)), p.Gross, p.Deductions, p.Net);
        }).ToList();
        var totals = columns.ToDictionary(c => c.Code, c => rows.Sum(r => r.Amounts.GetValueOrDefault(c.Code)));
        return new PayrollRegister(run.Id, run.RunNo, run.Year, run.Month, run.Status.ToString(), columns, rows, totals, rows.Sum(r => r.Gross),
            rows.Sum(r => r.Deductions), rows.Sum(r => r.Net));
    }

    public async Task<(byte[] Content, string FileName)> RegisterExcelAsync(Guid runId, CancellationToken ct)
    {
        var r = await RegisterAsync(runId, ct);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add($"Payroll {r.Year}-{r.Month:D2}");
        var headers = new List<string> { "Emp No", "Name", "الاسم", "Department", "WPS", "Paid days" };
        headers.AddRange(r.Columns.Select(c => $"{c.NameEn} / {c.NameAr}"));
        headers.AddRange(["Gross", "Deductions", "Net"]);
        for (var i = 0; i < headers.Count; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var x in r.Rows)
        {
            var col = 1;
            sheet.Cell(row, col++).Value = x.EmpNo;
            sheet.Cell(row, col++).Value = x.NameEn;
            sheet.Cell(row, col++).Value = x.NameAr;
            sheet.Cell(row, col++).Value = x.DepartmentEn ?? "";
            sheet.Cell(row, col++).Value = x.InWps ? "Yes" : "No";
            sheet.Cell(row, col++).Value = x.PaidDays;
            foreach (var c in r.Columns) sheet.Cell(row, col++).Value = x.Amounts.GetValueOrDefault(c.Code);
            sheet.Cell(row, col++).Value = x.Gross;
            sheet.Cell(row, col++).Value = x.Deductions;
            sheet.Cell(row, col).Value = x.Net;
            row++;
        }
        sheet.Cell(row, 1).Value = "Total / الإجمالي";
        var totalCol = 7;
        foreach (var c in r.Columns) sheet.Cell(row, totalCol++).Value = r.Totals[c.Code];
        sheet.Cell(row, totalCol++).Value = r.Gross;
        sheet.Cell(row, totalCol++).Value = r.Deductions;
        sheet.Cell(row, totalCol).Value = r.Net;
        sheet.Row(row).Style.Font.Bold = true;
        sheet.Range(2, 6, row, headers.Count).Style.NumberFormat.Format = "#,##0.00";
        sheet.Columns().AdjustToContents();
        sheet.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return (stream.ToArray(), $"payroll_register_{r.Year}{r.Month:D2}.xlsx");
    }

    public async Task<Headcount> HeadcountAsync(DateOnly? asOf, CancellationToken ct)
    {
        var date = asOf ?? clock.Today;
        var yearStart = new DateOnly(date.Year, 1, 1);
        var all = await db.Set<Employee>().AsNoTracking().Where(e => e.CompanyId == tenant.RequireCompanyId()).ToListAsync(ct);
        var active = all.Where(e => e.JoiningDate <= date && (e.TerminationDate is null || e.TerminationDate >= date)).ToList();
        var departments = await db.Set<Department>().AsNoTracking().Where(d => d.CompanyId == tenant.RequireCompanyId()).ToDictionaryAsync(d => d.Id, ct);
        CountRow Row(string key, string en, string ar, List<Employee> group) =>
            new(key, en, ar, group.Count, group.Count(e => e.Gender == Gender.Male), group.Count(e => e.Gender == Gender.Female));
        var byNationality = active.GroupBy(e => e.Nationality).Select(g => Row(g.Key, g.Key, g.Key, g.ToList())).OrderByDescending(r => r.Count).ToList();
        var byDepartment = active.GroupBy(e => e.DepartmentId)
            .Select(g => g.Key is { } id && departments.TryGetValue(id, out var d) ? Row(d.Code, d.NameEn, d.NameAr, g.ToList()) : Row("", "No department", "بدون قسم", g.ToList()))
            .OrderByDescending(r => r.Count).ToList();
        var qatari = active.Count(e => e.Nationality == "QA");
        return new Headcount(date, active.Count, qatari, active.Count == 0 ? 0 : Math.Round(qatari * 100m / active.Count, 1),
            all.Count(e => e.JoiningDate >= yearStart && e.JoiningDate <= date),
            all.Count(e => e.TerminationDate is { } t && t >= yearStart && t <= date), byNationality, byDepartment);
    }

    /// <summary>
    /// Unused annual leave valued at the basic daily rate (basic ÷ 30), next to the leave-salary provision posted by payroll and the
    /// ledger balance of the provision account.
    /// </summary>
    public async Task<LeaveLiability> LeaveLiabilityAsync(DateOnly? asOf, CancellationToken ct)
    {
        var date = asOf ?? clock.Today;
        var companyId = tenant.RequireCompanyId();
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.JoiningDate <= date && (e.Status == EmployeeStatus.Active || e.TerminationDate >= date))
            .OrderBy(e => e.EmpNo).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var basics = await (from c in db.Set<EmploymentContract>().AsNoTracking()
                            join cc in db.Set<ContractComponent>() on c.Id equals cc.ContractId
                            join pc in db.Set<PayComponent>() on cc.PayComponentId equals pc.Id
                            where ids.Contains(c.EmployeeId) && c.Status == ContractStatus.Active && pc.WpsField == WpsField.Basic
                            select new { c.EmployeeId, cc.Amount }).ToListAsync(ct);
        var basicByEmployee = basics.GroupBy(b => b.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var provisions = (await db.Set<BenefitAccrual>().AsNoTracking()
                .Where(a => ids.Contains(a.EmployeeId) && a.Kind == BenefitKind.LeaveSalary && a.Status == AccrualStatus.Posted && a.PeriodEnd <= date)
                .Select(a => new { a.EmployeeId, a.PeriodEnd, a.Liability }).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.PeriodEnd)!.Liability);
        var rows = new List<LeaveLiabilityRow>();
        foreach (var e in employees)
        {
            if (!basicByEmployee.TryGetValue(e.Id, out var basic)) continue;
            var balance = LeaveBalanceDays(await leave.BalanceAsync(e.Id, date, ct));
            var rate = Math.Round(basic / 30m, 4, MidpointRounding.AwayFromZero);
            rows.Add(new LeaveLiabilityRow(e.Id, e.EmpNo, e.NameEn, e.NameAr, balance, rate, Money.Round(Math.Max(balance, 0) * rate),
                provisions.GetValueOrDefault(e.Id)));
        }
        var ledger = -await finance.GetRoleBalanceAsync(AccountRoles.LeaveSalaryProvision, date, ct);
        return new LeaveLiability(date, rows, rows.Sum(r => r.Liability), rows.Sum(r => r.Provision), ledger);
    }

    /// <summary>Per payroll run: payroll net for WPS employees against the SIF file, employees paid outside WPS, and the payment.</summary>
    public async Task<List<WpsReconciliationRow>> WpsReconciliationAsync(int year, CancellationToken ct)
    {
        var runs = await db.Set<PayrollRun>().AsNoTracking()
            .Where(r => r.CompanyId == tenant.RequireCompanyId() && r.Year == year && r.Status != PayrollStatus.Reversed)
            .OrderBy(r => r.Month).ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var payslips = await db.Set<Payslip>().AsNoTracking().Where(p => runIds.Contains(p.PayrollRunId))
            .Select(p => new { p.PayrollRunId, p.InWps, p.Net }).ToListAsync(ct);
        var files = (await db.Set<WpsFile>().AsNoTracking().Where(f => runIds.Contains(f.PayrollRunId)).ToListAsync(ct))
            .GroupBy(f => f.PayrollRunId).ToDictionary(g => g.Key, g => g.MaxBy(f => f.CreatedAt)!);
        return runs.Select(r =>
        {
            var mine = payslips.Where(p => p.PayrollRunId == r.Id).ToList();
            var wps = mine.Where(p => p.InWps).ToList();
            var outside = mine.Where(p => !p.InWps).ToList();
            files.TryGetValue(r.Id, out var file);
            var issues = new List<string>();
            var wpsNet = wps.Sum(p => p.Net);
            var posted = r.Status is PayrollStatus.Posted or PayrollStatus.Paid;
            if (posted && file is null) issues.Add("no_sif");
            if (file is not null && file.TotalSalaries != wpsNet) issues.Add("sif_total");
            if (file is not null && file.RecordCount != wps.Count) issues.Add("sif_records");
            if (r.Status == PayrollStatus.Posted && clock.Today > r.WpsDeadline) issues.Add("late");
            return new WpsReconciliationRow(r.Id, r.RunNo, r.Year, r.Month, r.Status.ToString(), r.WpsDeadline, mine.Sum(p => p.Net), wpsNet, wps.Count,
                outside.Sum(p => p.Net), outside.Count, file?.TotalSalaries, file?.RecordCount, file?.Status.ToString(), r.PaymentDate, issues.Count == 0, issues);
        }).ToList();
    }

    private static decimal LeaveBalanceDays(object balance) =>
        balance.GetType().GetProperty("balance")?.GetValue(balance) is decimal d ? d : 0m;
}

internal static class HrReportEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hr/reports").WithTags("HR & Payroll").RequireAuthorization();
        g.MapGet("/payroll-register", (Guid runId, HrReportService s, CancellationToken ct) => s.RegisterAsync(runId, ct))
            .RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/payroll-register.xlsx", async (Guid runId, HrReportService s, CancellationToken ct) =>
        {
            var (content, name) = await s.RegisterExcelAsync(runId, ct);
            return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/headcount", (HrReportService s, CancellationToken ct, DateOnly? asOf = null) => s.HeadcountAsync(asOf, ct))
            .RequirePermission(HrPermissions.EmployeesRead);
        g.MapGet("/leave-liability", (HrReportService s, CancellationToken ct, DateOnly? asOf = null) => s.LeaveLiabilityAsync(asOf, ct))
            .RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/wps-reconciliation", (int year, HrReportService s, CancellationToken ct) => s.WpsReconciliationAsync(year, ct))
            .RequirePermission(HrPermissions.PayrollRead);
    }
}

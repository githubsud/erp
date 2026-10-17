using System.Text;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.HR.Documents;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;
using Erp.Modules.HR.People;
using Erp.Modules.HR.Wps;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Endpoints;

public sealed record HrSettingsDto(
    string? EmployerEid, string? PayerEid, string? PayerQid, string? PayerBankShortName, string? PayerIban, string SifFormatVersion,
    decimal GratuityDaysPerYear, decimal GratuityDivisor, int GratuityMinServiceMonths, decimal DailyRateDivisor, decimal HoursPerDay,
    decimal OvertimeNormalRate, decimal OvertimeNightRate, decimal OvertimeRestDayRate, decimal OvertimeHolidayRate,
    decimal DeductionCapPercent, decimal MinimumBasicWage, decimal MinimumHousingAllowance, decimal MinimumFoodAllowance, int WpsDepositDays,
    GratuityBasis LeaveSalaryBasis = GratuityBasis.Basic);

public sealed record DepartmentDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive);
public sealed record CreateDepartmentRequest(string Code, string? NameEn, string? NameAr);
public sealed record PayComponentDto(Guid Id, string Code, string NameEn, string NameAr, string Kind, string WpsField, bool IsFixed,
    bool ProrateByDays, bool InGratuityBasis, bool IsSystem, bool IsActive);

public sealed record EmployeeDto(
    Guid Id, string EmpNo, string NameEn, string NameAr, string Nationality, string Gender, DateOnly? DateOfBirth, DateOnly JoiningDate,
    string Status, DateOnly? TerminationDate, Guid? DepartmentId, Guid? BranchId, Guid? CostCenterId, string? JobTitleEn, string? JobTitleAr,
    string? Mobile, string? Email, string? Qid, string? VisaId, string? PassportNo, string? BankShortName, string? Iban, bool InWps,
    string SocialInsuranceScheme, decimal? OpeningGratuityLiability, bool SensitiveMasked);

public sealed record ContractDto(Guid Id, DateOnly StartDate, DateOnly? EndDate, string ContractType, int ProbationMonths,
    bool AccommodationProvided, bool FoodProvided, decimal? GratuityDaysPerYear, string GratuityBasis, int? AnnualLeaveDays, string Status,
    IReadOnlyList<ContractComponentDto> Components, decimal MonthlyGross);
public sealed record ContractComponentDto(string Code, string NameEn, string NameAr, decimal Amount);

public sealed record DocumentDto(Guid Id, string DocType, string? DocNo, DateOnly? IssueDate, DateOnly? ExpiryDate, string? Notes);
public sealed record CompanyDocumentDto(Guid Id, string DocType, string NameEn, string NameAr, string? DocNo, DateOnly? IssueDate,
    DateOnly? ExpiryDate, Guid? ResponsibleUserId, string? Notes);
public sealed record LeaveDto(Guid Id, Guid EmployeeId, string LeaveTypeCode, DateOnly FromDate, DateOnly ToDate, decimal Days, string Status, string? Reason);
public sealed record PayrollInputDto(Guid Id, Guid EmployeeId, int Year, int Month, string ComponentCode, string? OvertimeType,
    decimal? Hours, decimal? Amount, string? Notes);

public sealed record PayrollRunDto(Guid Id, string RunNo, int Year, int Month, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate,
    DateOnly WpsDeadline, string Status, int EmployeeCount, decimal TotalGross, decimal TotalDeductions, decimal TotalNet,
    decimal TotalGratuityAccrual, DateTimeOffset? CalculatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? PostedAt, DateOnly? PaymentDate,
    Guid? JournalEntryId, Guid? GratuityJournalEntryId, Guid? PaymentJournalEntryId);
public sealed record PayslipSummaryDto(Guid Id, Guid EmployeeId, string EmpNo, string NameEn, string NameAr, decimal PaidDays,
    decimal UnpaidDays, decimal Gross, decimal Deductions, decimal Net, bool InWps, string? Warnings);
public sealed record PayslipLineDto(int LineNo, string Code, string NameEn, string NameAr, string Kind, decimal? Quantity, decimal? Rate, decimal Amount);
public sealed record PayslipDto(PayslipSummaryDto Summary, decimal BasicSalary, decimal OvertimeHours, IReadOnlyList<PayslipLineDto> Lines);
public sealed record PayrollRunDetail(PayrollRunDto Run, IReadOnlyList<PayslipSummaryDto> Payslips);
public sealed record WpsFileDto(Guid Id, string FileName, string FormatVersion, string PayerBankShortName, decimal TotalSalaries,
    int RecordCount, string Sha256, string Status, DateTimeOffset CreatedAt);
public sealed record SettlementLineDto(int LineNo, string Code, string NameEn, string NameAr, string Kind, decimal? Quantity, decimal? Rate, decimal Amount);
public sealed record SettlementDto(Guid Id, string SettlementNo, Guid EmployeeId, string EmpNo, string NameEn, string NameAr, DateOnly LastWorkingDay,
    string Reason, string Status, DateOnly JoiningDate, int ServiceDays, int ExcludedDays, decimal BasisWage, bool EligibleForGratuity,
    decimal GratuityAmount, decimal GratuityProvision, decimal LeaveBalanceDays, decimal DailyLeaveRate, DateOnly? UnpaidSalaryFrom,
    decimal UnpaidSalaryDays, decimal TotalEarnings, decimal TotalDeductions, decimal NetPayable, string? Notes, Guid? JournalEntryId,
    DateOnly? PaymentDate, IReadOnlyList<SettlementLineDto> Lines);

public sealed record GratuityLiabilityRow(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, DateOnly JoiningDate,
    decimal BasisWage, int ServiceDays, decimal Liability, decimal Provision, bool EligibleForPayout);

internal static class HrEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hr").WithTags("HR & Payroll").RequireAuthorization();

        g.MapGet("/settings", GetSettings).RequirePermission(HrPermissions.PayrollRead);
        g.MapPut("/settings", UpdateSettings).RequirePermission(HrPermissions.SettingsManage);
        g.MapGet("/departments", ListDepartments).RequirePermission(HrPermissions.EmployeesRead);
        g.MapPost("/departments", CreateDepartment).RequirePermission(HrPermissions.EmployeesManage);
        g.MapGet("/pay-components", ListComponents).RequirePermission(HrPermissions.EmployeesRead);

        g.MapGet("/employees", ListEmployees).RequirePermission(HrPermissions.EmployeesRead);
        g.MapGet("/employees/{id:guid}", GetEmployee).RequirePermission(HrPermissions.EmployeesRead);
        g.MapPost("/employees", async (EmployeeRequest r, EmployeeService s, IPermissionService p, CancellationToken ct) =>
            ToDto(await s.CreateAsync(r, ct), await p.HasAsync(HrPermissions.SensitiveView, ct))).RequirePermission(HrPermissions.EmployeesManage);
        g.MapPut("/employees/{id:guid}", async (Guid id, EmployeeRequest r, EmployeeService s, IPermissionService p, CancellationToken ct) =>
            ToDto(await s.UpdateAsync(id, r, ct), await p.HasAsync(HrPermissions.SensitiveView, ct))).RequirePermission(HrPermissions.EmployeesManage);
        g.MapPost("/employees/{id:guid}/terminate", async (Guid id, TerminateRequest r, EmployeeService s, IPermissionService p, CancellationToken ct) =>
            ToDto(await s.TerminateAsync(id, r, ct), await p.HasAsync(HrPermissions.SensitiveView, ct))).RequirePermission(HrPermissions.EmployeesManage);

        g.MapGet("/employees/{id:guid}/contracts", ListContracts).RequirePermission(HrPermissions.SensitiveView);
        g.MapPost("/employees/{id:guid}/contracts", async (Guid id, ContractRequest r, ContractService s, ErpDbContext db, CancellationToken ct) =>
        {
            var contract = await s.CreateAsync(id, r, ct);
            return (await ContractsOf(db, id, ct)).Single(c => c.Id == contract.Id);
        }).RequirePermission(HrPermissions.ContractsManage);

        g.MapGet("/employees/{id:guid}/documents", ListEmployeeDocuments).RequirePermission(HrPermissions.EmployeesRead);
        g.MapPost("/employees/{id:guid}/documents", async (Guid id, EmployeeDocumentRequest r, DocumentService s, CancellationToken ct) =>
            ToDto(await s.AddEmployeeDocumentAsync(id, r, ct))).RequirePermission(HrPermissions.DocumentsManage);
        g.MapPut("/employee-documents/{id:guid}", async (Guid id, EmployeeDocumentRequest r, DocumentService s, CancellationToken ct) =>
            ToDto(await s.UpdateEmployeeDocumentAsync(id, r, ct))).RequirePermission(HrPermissions.DocumentsManage);
        g.MapGet("/company-documents", ListCompanyDocuments).RequirePermission(HrPermissions.EmployeesRead);
        g.MapPost("/company-documents", async (CompanyDocumentRequest r, DocumentService s, CancellationToken ct) =>
            ToDto(await s.SaveCompanyDocumentAsync(null, r, ct))).RequirePermission(HrPermissions.DocumentsManage);
        g.MapPut("/company-documents/{id:guid}", async (Guid id, CompanyDocumentRequest r, DocumentService s, CancellationToken ct) =>
            ToDto(await s.SaveCompanyDocumentAsync(id, r, ct))).RequirePermission(HrPermissions.DocumentsManage);
        g.MapGet("/documents/expiring", (DocumentService s, CancellationToken ct, int withinDays = 90) => s.ExpiringAsync(withinDays, ct))
            .RequirePermission(HrPermissions.EmployeesRead);

        g.MapGet("/leave-types", ListLeaveTypes).RequirePermission(HrPermissions.EmployeesRead);
        g.MapGet("/leave-requests", ListLeave).RequirePermission(HrPermissions.EmployeesRead);
        g.MapPost("/leave-requests", async (LeaveRequestCreate r, LeaveService s, ErpDbContext db, CancellationToken ct) =>
            await LeaveDtoAsync(db, await s.CreateAsync(r, ct), ct)).RequirePermission(HrPermissions.LeaveManage);
        g.MapPost("/leave-requests/{id:guid}/approve", async (Guid id, LeaveService s, ErpDbContext db, CancellationToken ct) =>
            await LeaveDtoAsync(db, await s.DecideAsync(id, LeaveStatus.Approved, ct), ct)).RequirePermission(HrPermissions.LeaveApprove);
        g.MapPost("/leave-requests/{id:guid}/reject", async (Guid id, LeaveService s, ErpDbContext db, CancellationToken ct) =>
            await LeaveDtoAsync(db, await s.DecideAsync(id, LeaveStatus.Rejected, ct), ct)).RequirePermission(HrPermissions.LeaveApprove);
        g.MapPost("/leave-requests/{id:guid}/cancel", async (Guid id, LeaveService s, ErpDbContext db, CancellationToken ct) =>
            await LeaveDtoAsync(db, await s.DecideAsync(id, LeaveStatus.Cancelled, ct), ct)).RequirePermission(HrPermissions.LeaveManage);
        g.MapGet("/employees/{id:guid}/leave-balance", (Guid id, LeaveService s, IClock clock, CancellationToken ct, DateOnly? asOf = null) =>
            s.BalanceAsync(id, asOf ?? clock.Today, ct)).RequirePermission(HrPermissions.EmployeesRead);

        g.MapGet("/payroll-inputs", ListInputs).RequirePermission(HrPermissions.PayrollRead);
        g.MapPost("/payroll-inputs", async (PayrollInputRequest r, PayrollInputService s, ErpDbContext db, CancellationToken ct) =>
        {
            var input = await s.CreateAsync(r, ct);
            return (await InputsOf(db, input.Year, input.Month, ct)).Single(i => i.Id == input.Id);
        }).RequirePermission(HrPermissions.PayrollPrepare);
        g.MapDelete("/payroll-inputs/{id:guid}", async (Guid id, PayrollInputService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(HrPermissions.PayrollPrepare);

        g.MapGet("/payroll-runs", ListRuns).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/payroll-runs/{id:guid}", GetRun).RequirePermission(HrPermissions.PayrollRead);
        g.MapPost("/payroll-runs", async (CreatePayrollRunRequest r, PayrollService s, CancellationToken ct) => ToDto(await s.CreateAsync(r, ct)))
            .RequirePermission(HrPermissions.PayrollPrepare);
        g.MapPost("/payroll-runs/{id:guid}/calculate", async (Guid id, PayrollService s, CancellationToken ct) => ToDto(await s.CalculateAsync(id, ct)))
            .RequirePermission(HrPermissions.PayrollPrepare);
        g.MapPost("/payroll-runs/{id:guid}/approve", async (Guid id, PayrollService s, CancellationToken ct) => ToDto(await s.ApproveAsync(id, ct)))
            .RequirePermission(HrPermissions.PayrollApprove);
        g.MapPost("/payroll-runs/{id:guid}/post", async (Guid id, PayrollService s, CancellationToken ct) => ToDto(await s.PostAsync(id, ct)))
            .RequirePermission(HrPermissions.PayrollPost);
        g.MapPost("/payroll-runs/{id:guid}/mark-paid", async (Guid id, MarkPaidRequest r, PayrollService s, CancellationToken ct) =>
            ToDto(await s.MarkPaidAsync(id, r, ct))).RequirePermission(HrPermissions.PayrollPost);
        g.MapPost("/payroll-runs/{id:guid}/reverse", async (Guid id, ReversePayrollRequest r, PayrollService s, CancellationToken ct) =>
            ToDto(await s.ReverseAsync(id, r, ct))).RequirePermission(HrPermissions.PayrollPost);
        g.MapGet("/payroll-runs/{id:guid}/payslips/{payslipId:guid}", GetPayslip).RequirePermission(HrPermissions.PayrollRead);

        g.MapPost("/payroll-runs/{id:guid}/wps-files", async (Guid id, PayrollService s, CancellationToken ct) => ToDto(await s.GenerateWpsAsync(id, ct)))
            .RequirePermission(HrPermissions.PayrollPost);
        g.MapGet("/payroll-runs/{id:guid}/wps-files", ListWpsFiles).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/wps-files/{id:guid}/download", DownloadWps).RequirePermission(HrPermissions.PayrollPost);

        g.MapGet("/payroll-runs/{id:guid}/payslips/{payslipId:guid}/pdf", async (Guid id, Guid payslipId, HrPdfDocuments pdf, CancellationToken ct) =>
        {
            var (content, name) = await pdf.PayslipsAsync(id, payslipId, ct);
            return Results.File(content, "application/pdf", name);
        }).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/payroll-runs/{id:guid}/payslips.pdf", async (Guid id, HrPdfDocuments pdf, CancellationToken ct) =>
        {
            var (content, name) = await pdf.PayslipsAsync(id, null, ct);
            return Results.File(content, "application/pdf", name);
        }).RequirePermission(HrPermissions.PayrollRead);

        g.MapGet("/employees/{id:guid}/advances", async (Guid id, ErpDbContext db, CancellationToken ct) =>
            (await db.Set<EmployeeAdvance>().AsNoTracking().Where(a => a.EmployeeId == id).OrderByDescending(a => a.AdvanceDate).ToListAsync(ct))
            .Select(AdvanceService.ToDto).ToList()).RequirePermission(HrPermissions.PayrollRead);
        g.MapPost("/employees/{id:guid}/advances", async (Guid id, CreateAdvanceRequest r, AdvanceService s, CancellationToken ct) =>
            AdvanceService.ToDto(await s.CreateAsync(id, r, ct))).RequirePermission(HrPermissions.PayrollPost);
        g.MapGet("/employees/{id:guid}/advance-balance", (Guid id, AdvanceService s, IClock clock, CancellationToken ct, DateOnly? asOf = null) =>
            s.BalanceAsync(id, asOf ?? clock.Today, ct)).RequirePermission(HrPermissions.PayrollRead);

        g.MapGet("/final-settlements", ListSettlements).RequirePermission(HrPermissions.PayrollRead);
        g.MapGet("/final-settlements/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await SettlementsAsync(db, t, id, ct)).SingleOrDefault() ?? throw new NotFoundException("Final settlement", id))
            .RequirePermission(HrPermissions.PayrollRead);
        g.MapPost("/employees/{id:guid}/final-settlements", async (Guid id, CreateSettlementRequest r, FinalSettlementService s, ErpDbContext db,
            ITenantContext t, CancellationToken ct) => await SettlementDtoAsync(db, t, (await s.CreateAsync(id, r, ct)).Id, ct))
            .RequirePermission(HrPermissions.PayrollPrepare);
        g.MapDelete("/final-settlements/{id:guid}", async (Guid id, FinalSettlementService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(HrPermissions.PayrollPrepare);
        g.MapPost("/final-settlements/{id:guid}/post", async (Guid id, FinalSettlementService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await SettlementDtoAsync(db, t, (await s.PostAsync(id, ct)).Id, ct)).RequirePermission(HrPermissions.PayrollPost);
        g.MapPost("/final-settlements/{id:guid}/mark-paid", async (Guid id, MarkPaidRequest r, FinalSettlementService s, ErpDbContext db, ITenantContext t,
            CancellationToken ct) => await SettlementDtoAsync(db, t, (await s.MarkPaidAsync(id, r, ct)).Id, ct)).RequirePermission(HrPermissions.PayrollPost);
        g.MapPost("/final-settlements/{id:guid}/reverse", async (Guid id, ReversePayrollRequest r, FinalSettlementService s, ErpDbContext db, ITenantContext t,
            CancellationToken ct) => await SettlementDtoAsync(db, t, (await s.ReverseAsync(id, r, ct)).Id, ct)).RequirePermission(HrPermissions.PayrollPost);
        g.MapGet("/final-settlements/{id:guid}/pdf", async (Guid id, HrPdfDocuments pdf, CancellationToken ct) =>
        {
            var (content, name) = await pdf.SettlementAsync(id, ct);
            return Results.File(content, "application/pdf", name);
        }).RequirePermission(HrPermissions.PayrollRead);

        g.MapGet("/reports/gratuity-liability", GratuityLiability).RequirePermission(HrPermissions.PayrollRead);
    }

    // Settings and masters ---------------------------------------------------------------------------

    private static async Task<HrSettingsDto> GetSettings(HrSettingsProvider provider, CancellationToken ct) => ToDto(await provider.GetAsync(ct));

    private static async Task<HrSettingsDto> UpdateSettings(HrSettingsDto r, HrSettingsProvider provider, ErpDbContext db, CancellationToken ct)
    {
        var s = await provider.GetAsync(ct);
        if (r.PayerIban is { Length: > 0 } && !QatarIdentifiers.IsValidQatarIban(r.PayerIban))
            throw new BusinessRuleException("hr.invalid_iban", "Payer IBAN is not a valid Qatari IBAN.", "آيبان جهة الدفع غير صحيح.");
        if (r.GratuityDaysPerYear < 21)
            throw new BusinessRuleException("hr.gratuity_minimum", "Gratuity cannot be less than 21 days (three weeks) per year of service.",
                "لا يجوز أن تقل مكافأة نهاية الخدمة عن 21 يوماً عن كل سنة خدمة.");
        if (r.DeductionCapPercent is <= 0 or > 100 || r.DailyRateDivisor <= 0 || r.HoursPerDay <= 0 || r.GratuityDivisor <= 0)
            throw new BusinessRuleException("hr.invalid_settings", "One or more payroll settings are out of range.", "بعض إعدادات الرواتب خارج النطاق المسموح.");

        s.EmployerEid = r.EmployerEid?.Trim();
        s.PayerEid = r.PayerEid?.Trim();
        s.PayerQid = r.PayerQid?.Trim();
        s.PayerBankShortName = r.PayerBankShortName?.Trim().ToUpperInvariant();
        s.PayerIban = r.PayerIban is null ? null : QatarIdentifiers.Normalize(r.PayerIban);
        s.SifFormatVersion = r.SifFormatVersion is "0" or "1" ? r.SifFormatVersion : "1";
        s.GratuityDaysPerYear = r.GratuityDaysPerYear;
        s.GratuityDivisor = r.GratuityDivisor;
        s.GratuityMinServiceMonths = r.GratuityMinServiceMonths;
        s.DailyRateDivisor = r.DailyRateDivisor;
        s.HoursPerDay = r.HoursPerDay;
        s.OvertimeNormalRate = r.OvertimeNormalRate;
        s.OvertimeNightRate = r.OvertimeNightRate;
        s.OvertimeRestDayRate = r.OvertimeRestDayRate;
        s.OvertimeHolidayRate = r.OvertimeHolidayRate;
        s.DeductionCapPercent = r.DeductionCapPercent;
        s.MinimumBasicWage = r.MinimumBasicWage;
        s.MinimumHousingAllowance = r.MinimumHousingAllowance;
        s.MinimumFoodAllowance = r.MinimumFoodAllowance;
        s.WpsDepositDays = r.WpsDepositDays;
        s.LeaveSalaryBasis = r.LeaveSalaryBasis;
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    private static HrSettingsDto ToDto(HrSettings s) => new(s.EmployerEid, s.PayerEid, s.PayerQid, s.PayerBankShortName, s.PayerIban,
        s.SifFormatVersion, s.GratuityDaysPerYear, s.GratuityDivisor, s.GratuityMinServiceMonths, s.DailyRateDivisor, s.HoursPerDay,
        s.OvertimeNormalRate, s.OvertimeNightRate, s.OvertimeRestDayRate, s.OvertimeHolidayRate, s.DeductionCapPercent, s.MinimumBasicWage,
        s.MinimumHousingAllowance, s.MinimumFoodAllowance, s.WpsDepositDays, s.LeaveSalaryBasis);

    private static async Task<List<DepartmentDto>> ListDepartments(ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        await db.Set<Department>().AsNoTracking().Where(d => d.CompanyId == t.RequireCompanyId()).OrderBy(d => d.Code)
            .Select(d => new DepartmentDto(d.Id, d.Code, d.NameEn, d.NameAr, d.IsActive)).ToListAsync(ct);

    private static async Task<DepartmentDto> CreateDepartment(CreateDepartmentRequest r, ErpDbContext db, ITenantContext t, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        var d = new Department { CompanyId = t.RequireCompanyId(), Code = Guard.Required(r.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar };
        db.Add(d);
        await db.SaveChangesAsync(ct);
        return new DepartmentDto(d.Id, d.Code, d.NameEn, d.NameAr, d.IsActive);
    }

    private static async Task<List<PayComponentDto>> ListComponents(ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        (await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == t.RequireCompanyId()).OrderBy(c => c.Kind).ThenBy(c => c.Code).ToListAsync(ct))
        .Select(c => new PayComponentDto(c.Id, c.Code, c.NameEn, c.NameAr, c.Kind.ToString(), c.WpsField.ToString(), c.IsFixed, c.ProrateByDays,
            c.InGratuityBasis, c.IsSystem, c.IsActive)).ToList();

    // Employees ---------------------------------------------------------------------------------------

    private static async Task<PagedResult<EmployeeDto>> ListEmployees(ErpDbContext db, ITenantContext t, IPermissionService p, CancellationToken ct,
        string? search = null, string? status = null, int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.Set<Employee>().AsNoTracking().Where(e => e.CompanyId == t.RequireCompanyId());
        if (Enum.TryParse<EmployeeStatus>(status, true, out var s)) q = q.Where(e => e.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var qidHash = QatarIdentifiers.IsValidQid(term) ? Erp.Infrastructure.Security.FieldProtector.BlindIndex(QatarIdentifiers.Normalize(term)) : null;
            q = q.Where(e => EF.Functions.ILike(e.NameEn, $"%{term}%") || e.NameAr.Contains(term) || EF.Functions.ILike(e.EmpNo, $"%{term}%")
                             || (qidHash != null && e.QidHash == qidHash));
        }
        var total = await q.CountAsync(ct);
        var showSensitive = await p.HasAsync(HrPermissions.SensitiveView, ct);
        var items = await q.OrderBy(e => e.EmpNo).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<EmployeeDto>(items.Select(e => ToDto(e, showSensitive)).ToList(), total, page, pageSize);
    }

    private static async Task<EmployeeDto> GetEmployee(Guid id, EmployeeService s, IPermissionService p, CancellationToken ct) =>
        ToDto(await s.FindAsync(id, ct), await p.HasAsync(HrPermissions.SensitiveView, ct));

    private static EmployeeDto ToDto(Employee e, bool showSensitive) => new(e.Id, e.EmpNo, e.NameEn, e.NameAr, e.Nationality, e.Gender.ToString(),
        e.DateOfBirth, e.JoiningDate, e.Status.ToString(), e.TerminationDate, e.DepartmentId, e.BranchId, e.CostCenterId, e.JobTitleEn, e.JobTitleAr,
        e.Mobile, e.Email, Mask(e.Qid, showSensitive), e.VisaId, Mask(e.PassportNo, showSensitive), e.BankShortName, Mask(e.Iban, showSensitive),
        e.InWps, e.SocialInsuranceScheme.ToString(), showSensitive ? e.OpeningGratuityLiability : null, !showSensitive);

    internal static string? Mask(string? value, bool show) =>
        value is null || show ? value : value.Length <= 4 ? new string('•', value.Length) : new string('•', value.Length - 4) + value[^4..];

    private static Task<List<ContractDto>> ListContracts(Guid id, ErpDbContext db, CancellationToken ct) => ContractsOf(db, id, ct);

    private static async Task<List<ContractDto>> ContractsOf(ErpDbContext db, Guid employeeId, CancellationToken ct)
    {
        var contracts = await db.Set<EmploymentContract>().AsNoTracking().Include(c => c.Components)
            .Where(c => c.EmployeeId == employeeId).OrderByDescending(c => c.StartDate).ToListAsync(ct);
        var componentIds = contracts.SelectMany(c => c.Components).Select(c => c.PayComponentId).Distinct().ToList();
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        return contracts.Select(c => new ContractDto(c.Id, c.StartDate, c.EndDate, c.ContractType.ToString(), c.ProbationMonths, c.AccommodationProvided,
            c.FoodProvided, c.GratuityDaysPerYear, c.GratuityBasis.ToString(), c.AnnualLeaveDays, c.Status.ToString(),
            c.Components.Select(x => new ContractComponentDto(components[x.PayComponentId].Code, components[x.PayComponentId].NameEn,
                components[x.PayComponentId].NameAr, x.Amount)).OrderBy(x => x.Code).ToList(),
            c.Components.Sum(x => x.Amount))).ToList();
    }

    // Documents ---------------------------------------------------------------------------------------

    private static async Task<List<DocumentDto>> ListEmployeeDocuments(Guid id, ErpDbContext db, CancellationToken ct) =>
        (await db.Set<EmployeeDocument>().AsNoTracking().Where(d => d.EmployeeId == id).OrderBy(d => d.ExpiryDate).ToListAsync(ct)).Select(ToDto).ToList();

    private static DocumentDto ToDto(EmployeeDocument d) => new(d.Id, d.DocType.ToString(), d.DocNo, d.IssueDate, d.ExpiryDate, d.Notes);

    private static async Task<List<CompanyDocumentDto>> ListCompanyDocuments(ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        (await db.Set<CompanyDocument>().AsNoTracking().Where(d => d.CompanyId == t.RequireCompanyId()).OrderBy(d => d.ExpiryDate).ToListAsync(ct))
        .Select(ToDto).ToList();

    private static CompanyDocumentDto ToDto(CompanyDocument d) =>
        new(d.Id, d.DocType.ToString(), d.NameEn, d.NameAr, d.DocNo, d.IssueDate, d.ExpiryDate, d.ResponsibleUserId, d.Notes);

    // Leave ------------------------------------------------------------------------------------------

    private static async Task<object> ListLeaveTypes(ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        await db.Set<LeaveType>().AsNoTracking().Where(l => l.CompanyId == t.RequireCompanyId()).OrderBy(l => l.Code)
            .Select(l => new { l.Id, l.Code, l.NameEn, l.NameAr, l.IsPaid, l.AffectsService, l.DeductsAnnualBalance }).ToListAsync(ct);

    private static async Task<List<LeaveDto>> ListLeave(ErpDbContext db, CancellationToken ct, Guid? employeeId = null, string? status = null)
    {
        var q = from l in db.Set<LeaveRequest>().AsNoTracking()
                join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
                select new { l, t.Code };
        if (employeeId is not null) q = q.Where(x => x.l.EmployeeId == employeeId);
        if (Enum.TryParse<LeaveStatus>(status, true, out var s)) q = q.Where(x => x.l.Status == s);
        return (await q.OrderByDescending(x => x.l.FromDate).Take(500).ToListAsync(ct))
            .Select(x => new LeaveDto(x.l.Id, x.l.EmployeeId, x.Code, x.l.FromDate, x.l.ToDate, x.l.Days, x.l.Status.ToString(), x.l.Reason)).ToList();
    }

    private static async Task<LeaveDto> LeaveDtoAsync(ErpDbContext db, LeaveRequest l, CancellationToken ct)
    {
        var code = await db.Set<LeaveType>().Where(t => t.Id == l.LeaveTypeId).Select(t => t.Code).SingleAsync(ct);
        return new LeaveDto(l.Id, l.EmployeeId, code, l.FromDate, l.ToDate, l.Days, l.Status.ToString(), l.Reason);
    }

    // Payroll -------------------------------------------------------------------------------------------

    private static Task<List<PayrollInputDto>> ListInputs(int year, int month, ErpDbContext db, CancellationToken ct) => InputsOf(db, year, month, ct);

    private static async Task<List<PayrollInputDto>> InputsOf(ErpDbContext db, int year, int month, CancellationToken ct) =>
        (await (from i in db.Set<PayrollInput>().AsNoTracking()
                join c in db.Set<PayComponent>() on i.PayComponentId equals c.Id
                where i.Year == year && i.Month == month
                select new { i, c.Code }).ToListAsync(ct))
        .Select(x => new PayrollInputDto(x.i.Id, x.i.EmployeeId, x.i.Year, x.i.Month, x.Code, x.i.OvertimeType?.ToString(), x.i.Hours, x.i.Amount, x.i.Notes))
        .ToList();

    private static async Task<List<PayrollRunDto>> ListRuns(ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        (await db.Set<PayrollRun>().AsNoTracking().Where(r => r.CompanyId == t.RequireCompanyId())
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ThenByDescending(r => r.CreatedAt).ToListAsync(ct))
        .Select(ToDto).ToList();

    private static async Task<PayrollRunDetail> GetRun(Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.CompanyId == t.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Payroll run", id);
        var payslips = await db.Set<Payslip>().AsNoTracking().Where(p => p.PayrollRunId == id).OrderBy(p => p.EmpNo).ToListAsync(ct);
        return new PayrollRunDetail(ToDto(run), payslips.Select(ToSummary).ToList());
    }

    private static async Task<PayslipDto> GetPayslip(Guid id, Guid payslipId, ErpDbContext db, CancellationToken ct)
    {
        var p = await db.Set<Payslip>().AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == payslipId && x.PayrollRunId == id, ct)
            ?? throw new NotFoundException("Payslip", payslipId);
        return new PayslipDto(ToSummary(p), p.BasicSalary, p.OvertimeHours,
            p.Lines.OrderBy(l => l.LineNo).Select(l => new PayslipLineDto(l.LineNo, l.Code, l.NameEn, l.NameAr, l.Kind.ToString(), l.Quantity, l.Rate, l.Amount)).ToList());
    }

    private static PayslipSummaryDto ToSummary(Payslip p) =>
        new(p.Id, p.EmployeeId, p.EmpNo, p.NameEn, p.NameAr, p.PaidDays, p.UnpaidDays, p.Gross, p.Deductions, p.Net, p.InWps, p.Warnings);

    private static PayrollRunDto ToDto(PayrollRun r) => new(r.Id, r.RunNo, r.Year, r.Month, r.PeriodStart, r.PeriodEnd, r.DueDate, r.WpsDeadline,
        r.Status.ToString(), r.EmployeeCount, r.TotalGross, r.TotalDeductions, r.TotalNet, r.TotalGratuityAccrual, r.CalculatedAt, r.ApprovedAt,
        r.PostedAt, r.PaymentDate, r.JournalEntryId, r.GratuityJournalEntryId, r.PaymentJournalEntryId);

    private static async Task<List<WpsFileDto>> ListWpsFiles(Guid id, ErpDbContext db, CancellationToken ct) =>
        (await db.Set<WpsFile>().AsNoTracking().Where(f => f.PayrollRunId == id).OrderByDescending(f => f.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();

    private static WpsFileDto ToDto(WpsFile f) =>
        new(f.Id, f.FileName, f.FormatVersion, f.PayerBankShortName, f.TotalSalaries, f.RecordCount, f.Sha256, f.Status.ToString(), f.CreatedAt);

    private static async Task<IResult> DownloadWps(Guid id, ErpDbContext db, CancellationToken ct)
    {
        var file = await db.Set<WpsFile>().AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("WPS file", id);
        return Results.File(Encoding.UTF8.GetBytes(file.Content), "text/csv", file.FileName);
    }

    private static Task<List<SettlementDto>> ListSettlements(ErpDbContext db, ITenantContext t, CancellationToken ct) => SettlementsAsync(db, t, null, ct);

    private static async Task<SettlementDto> SettlementDtoAsync(ErpDbContext db, ITenantContext t, Guid id, CancellationToken ct) =>
        (await SettlementsAsync(db, t, id, ct)).Single();

    private static async Task<List<SettlementDto>> SettlementsAsync(ErpDbContext db, ITenantContext t, Guid? id, CancellationToken ct)
    {
        var companyId = t.RequireCompanyId();
        var settlements = await db.Set<FinalSettlement>().AsNoTracking().Include(x => x.Lines)
            .Where(x => x.CompanyId == companyId && (id == null || x.Id == id))
            .OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct);
        var employeeIds = settlements.Select(x => x.EmployeeId).Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return settlements.Select(x => new SettlementDto(x.Id, x.SettlementNo, x.EmployeeId, employees[x.EmployeeId].EmpNo, employees[x.EmployeeId].NameEn,
            employees[x.EmployeeId].NameAr, x.LastWorkingDay, x.Reason.ToString(), x.Status.ToString(), x.JoiningDate, x.ServiceDays, x.ExcludedDays,
            x.BasisWage, x.EligibleForGratuity, x.GratuityAmount, x.GratuityProvision, x.LeaveBalanceDays, x.DailyLeaveRate, x.UnpaidSalaryFrom,
            x.UnpaidSalaryDays, x.TotalEarnings, x.TotalDeductions, x.NetPayable, x.Notes, x.JournalEntryId, x.PaymentDate,
            x.Lines.OrderBy(l => l.LineNo).Select(l => new SettlementLineDto(l.LineNo, l.Code, l.NameEn, l.NameAr, l.Kind.ToString(), l.Quantity, l.Rate, l.Amount)).ToList()))
            .ToList();
    }

    private static async Task<List<GratuityLiabilityRow>> GratuityLiability(ErpDbContext db, ITenantContext t, HrSettingsProvider settingsProvider,
        PayrollService payroll, IClock clock, CancellationToken ct, DateOnly? asOf = null)
    {
        var date = asOf ?? clock.Today;
        var settings = await settingsProvider.GetAsync(ct);
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == t.RequireCompanyId() && e.SocialInsuranceScheme == SocialInsuranceScheme.None && e.JoiningDate <= date
                        && (e.Status == EmployeeStatus.Active || e.TerminationDate >= date))
            .OrderBy(e => e.EmpNo).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var contracts = await payroll.LoadContractsAsync(ids, date, date, ct);
        var excluded = await payroll.LoadServiceExclusionsAsync(ids, date, ct);
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == t.RequireCompanyId()).ToDictionaryAsync(c => c.Id, ct);
        var provisions = (await db.Set<GratuityAccrual>().AsNoTracking()
                .Where(a => ids.Contains(a.EmployeeId) && a.Status == AccrualStatus.Posted && a.PeriodEnd <= date)
                .Select(a => new { a.EmployeeId, a.PeriodEnd, a.Liability }).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.PeriodEnd)!.Liability);

        return employees.Where(e => contracts.ContainsKey(e.Id)).Select(e =>
        {
            var contract = contracts[e.Id];
            var basis = contract.Components.Where(c => components[c.PayComponentId].WpsField == WpsField.Basic
                    || (contract.GratuityBasis == GratuityBasis.BasicPlusFixedAllowances && components[c.PayComponentId].InGratuityBasis))
                .Sum(c => c.Amount);
            var serviceDays = GratuityCalculator.ServiceDays(e.JoiningDate, date, excluded.GetValueOrDefault(e.Id));
            var liability = GratuityCalculator.Liability(basis, contract.GratuityDaysPerYear ?? settings.GratuityDaysPerYear, settings.GratuityDivisor, serviceDays);
            return new GratuityLiabilityRow(e.Id, e.EmpNo, e.NameEn, e.NameAr, e.JoiningDate, basis, serviceDays, liability,
                provisions.TryGetValue(e.Id, out var p) ? p : e.OpeningGratuityLiability,
                GratuityCalculator.IsEligibleForPayout(e.JoiningDate, date, settings.GratuityMinServiceMonths));
        }).ToList();
    }
}

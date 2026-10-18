using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.HR.Documents;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Endpoints;
using Erp.Modules.HR.Payroll;
using Erp.Modules.HR.People;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.SelfService;

public sealed record PortalAccessResult(string Username, string TemporaryPassword);

public sealed record MySalaryComponent(string Code, string NameEn, string NameAr, decimal Amount);

public sealed record MyProfile(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, string? JobTitleEn, string? JobTitleAr, string? DepartmentEn,
    string? DepartmentAr, string Nationality, DateOnly JoiningDate, string? Mobile, string? Email, string? Qid, string? PassportNo, string? BankShortName,
    string? Iban, IReadOnlyList<MySalaryComponent> Salary, decimal MonthlySalary, int? AnnualLeaveDays);

public sealed record MyPayslip(Guid Id, Guid RunId, int Year, int Month, decimal PaidDays, decimal Gross, decimal Deductions, decimal Net, DateOnly? PaymentDate);

public sealed record MyLeaveRequest(Guid Id, string LeaveTypeCode, string LeaveTypeEn, string LeaveTypeAr, DateOnly FromDate, DateOnly ToDate, decimal Days,
    string Status, string? Reason);

public sealed record MyLeaveType(string Code, string NameEn, string NameAr, bool IsPaid);

public sealed record MyLeave(object Balance, IReadOnlyList<MyLeaveRequest> Requests, IReadOnlyList<MyLeaveType> Types);

public sealed record MyLeaveCreate(string LeaveTypeCode, DateOnly FromDate, DateOnly ToDate, string? Reason);

public sealed record MyDocument(Guid Id, string DocType, string? DocNo, DateOnly? ExpiryDate, string Status, int? DaysLeft);

public sealed record MyAdvances(decimal Outstanding, IReadOnlyList<AdvanceDto> Advances);

/// <summary>HR gives an employee a self-service sign-in: the employee number as username and a temporary password.</summary>
internal sealed class PortalService(ErpDbContext db, ITenantContext tenant, IUserAccounts accounts)
{
    private static readonly HashSet<string> ArabicSpeaking =
        ["QA", "SA", "AE", "KW", "BH", "OM", "EG", "JO", "SY", "LB", "PS", "IQ", "YE", "SD", "TN", "MA", "DZ", "LY", "MR"];

    public Task<PortalAccessResult> GrantAsync(Guid employeeId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var employee = await db.Set<Employee>().SingleOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Employee", employeeId);
            if (employee.Status == EmployeeStatus.Terminated)
                throw new BusinessRuleException("hr.portal_terminated", "Former employees cannot be given portal access.", "لا يمكن منح الموظفين السابقين حق الدخول.");
            var account = await accounts.CreateOrResetAsync(employee.UserId, employee.EmpNo, employee.Email, employee.NameEn, employee.NameAr,
                ArabicSpeaking.Contains(employee.Nationality) ? "ar" : "en", DefaultRoles.Employee, token);
            employee.UserId = account.UserId;
            await db.SaveChangesAsync(token);
            return new PortalAccessResult(account.Username, account.TemporaryPassword);
        }, ct);

    /// <summary>Disables the sign-in and unlinks it; granting again later revives the same user with a new temporary password.</summary>
    public Task RevokeAsync(Guid employeeId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var employee = await db.Set<Employee>().SingleOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Employee", employeeId);
            if (employee.UserId is not { } userId)
                throw new BusinessRuleException("hr.portal_none", "This employee has no portal access.", "ليس لهذا الموظف حق دخول.");
            await accounts.SetActiveAsync(userId, false, token);
            employee.UserId = null;
            await db.SaveChangesAsync(token);
        }, ct);
}

/// <summary>
/// The signed-in employee's own records. Everything is looked up from the user in the token, never from request data, so an
/// employee can only ever see their own payslips, leave and documents.
/// </summary>
internal static class SelfServiceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/me").WithTags("Employee self-service").RequireAuthorization().RequirePermission(HrPermissions.SelfService);

        g.MapGet("/profile", ProfileAsync);
        g.MapGet("/payslips", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var me = await MeAsync(db, t, ct);
            return await (from p in db.Set<Payslip>().AsNoTracking()
                          join r in db.Set<PayrollRun>() on p.PayrollRunId equals r.Id
                          where p.EmployeeId == me.Id && (r.Status == PayrollStatus.Posted || r.Status == PayrollStatus.Paid)
                          orderby r.Year descending, r.Month descending
                          select new MyPayslip(p.Id, r.Id, r.Year, r.Month, p.PaidDays, p.Gross, p.Deductions, p.Net, r.PaymentDate)).ToListAsync(ct);
        });
        g.MapGet("/payslips/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var p = await OwnPayslipAsync(db, t, id, ct);
            return new PayslipDto(HrEndpoints.ToSummary(p), p.BasicSalary, p.OvertimeHours,
                p.Lines.OrderBy(l => l.LineNo).Select(l => new PayslipLineDto(l.LineNo, l.Code, l.NameEn, l.NameAr, l.Kind.ToString(), l.Quantity, l.Rate, l.Amount))
                    .ToList());
        });
        g.MapGet("/payslips/{id:guid}/pdf", async (Guid id, ErpDbContext db, ITenantContext t, HrPdfDocuments pdf, CancellationToken ct) =>
        {
            var p = await OwnPayslipAsync(db, t, id, ct);
            var (content, name) = await pdf.PayslipsAsync(p.PayrollRunId, p.Id, ct);
            return Results.File(content, "application/pdf", name);
        });

        g.MapGet("/leave", LeaveAsync);
        g.MapPost("/leave", async (MyLeaveCreate r, ErpDbContext db, ITenantContext t, LeaveService leave, CancellationToken ct) =>
        {
            var me = await MeAsync(db, t, ct);
            var created = await leave.CreateAsync(new LeaveRequestCreate(me.Id, r.LeaveTypeCode, r.FromDate, r.ToDate, r.Reason), ct);
            return Results.Ok(new { created.Id, Status = created.Status.ToString() });
        });
        g.MapPost("/leave/{id:guid}/cancel", async (Guid id, ErpDbContext db, ITenantContext t, LeaveService leave, CancellationToken ct) =>
        {
            var me = await MeAsync(db, t, ct);
            var request = await db.Set<LeaveRequest>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == id && l.EmployeeId == me.Id, ct)
                          ?? throw new NotFoundException("Leave request", id);
            if (request.Status != LeaveStatus.Pending)
                throw new BusinessRuleException("hr.leave_not_pending", "Only requests still waiting for approval can be cancelled here; ask HR.",
                    "يمكن إلغاء الطلبات قيد الانتظار فقط؛ تواصل مع الموارد البشرية.");
            await leave.DecideAsync(id, LeaveStatus.Cancelled, ct);
            return Results.NoContent();
        });

        g.MapGet("/documents", async (ErpDbContext db, ITenantContext t, IClock clock, CancellationToken ct) =>
        {
            var me = await MeAsync(db, t, ct);
            var today = clock.Today;
            return (await db.Set<EmployeeDocument>().AsNoTracking().Where(d => d.EmployeeId == me.Id).ToListAsync(ct))
                .Select(d =>
                {
                    int? left = d.ExpiryDate is { } e ? e.DayNumber - today.DayNumber : null;
                    var status = left switch { null => "NoExpiry", < 0 => "Expired", <= 60 => "Expiring", _ => "Valid" };
                    return new MyDocument(d.Id, d.DocType.ToString(), d.DocNo, d.ExpiryDate, status, left);
                })
                .OrderBy(d => d.DaysLeft ?? int.MaxValue).ToList();
        });

        g.MapGet("/advances", async (ErpDbContext db, ITenantContext t, AdvanceService advances, IClock clock, CancellationToken ct) =>
        {
            var me = await MeAsync(db, t, ct);
            var balance = await advances.BalanceAsync(me.Id, clock.Today, ct);
            var list = (await db.Set<EmployeeAdvance>().AsNoTracking().Where(a => a.EmployeeId == me.Id).OrderByDescending(a => a.AdvanceDate).ToListAsync(ct))
                .Select(AdvanceService.ToDto).ToList();
            return new MyAdvances(balance.Outstanding, list);
        });

        g.MapGet("/salary-certificate.pdf", async (ErpDbContext db, ITenantContext t, HrPdfDocuments pdf, CancellationToken ct, string? addressedTo = null) =>
        {
            var me = await MeAsync(db, t, ct);
            var (content, name) = await pdf.SalaryCertificateAsync(me.Id, addressedTo, ct);
            return Results.File(content, "application/pdf", name);
        });
    }

    internal static async Task<Employee> MeAsync(ErpDbContext db, ITenantContext t, CancellationToken ct)
    {
        var userId = t.UserId ?? throw new ForbiddenException();
        return await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.UserId == userId && e.CompanyId == t.RequireCompanyId(), ct)
               ?? throw new BusinessRuleException("hr.portal_not_linked", "Your sign-in is not linked to an employee record. Ask HR.",
                   "حسابك غير مرتبط بسجل موظف. تواصل مع الموارد البشرية.");
    }

    private static async Task<Payslip> OwnPayslipAsync(ErpDbContext db, ITenantContext t, Guid id, CancellationToken ct)
    {
        var me = await MeAsync(db, t, ct);
        return await (from p in db.Set<Payslip>().AsNoTracking().Include(x => x.Lines)
                      join r in db.Set<PayrollRun>() on p.PayrollRunId equals r.Id
                      where p.Id == id && p.EmployeeId == me.Id && (r.Status == PayrollStatus.Posted || r.Status == PayrollStatus.Paid)
                      select p).SingleOrDefaultAsync(ct)
               ?? throw new NotFoundException("Payslip", id);
    }

    private static async Task<MyProfile> ProfileAsync(ErpDbContext db, ITenantContext t, CancellationToken ct)
    {
        var me = await MeAsync(db, t, ct);
        var department = me.DepartmentId is { } d ? await db.Set<Department>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == d, ct) : null;
        var contract = await db.Set<EmploymentContract>().AsNoTracking().Include(c => c.Components)
            .Where(c => c.EmployeeId == me.Id && c.Status == ContractStatus.Active).OrderByDescending(c => c.StartDate).FirstOrDefaultAsync(ct);
        var componentIds = contract?.Components.Select(c => c.PayComponentId).ToList() ?? [];
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var salary = contract?.Components.Select(c => new MySalaryComponent(components[c.PayComponentId].Code, components[c.PayComponentId].NameEn,
            components[c.PayComponentId].NameAr, c.Amount)).ToList() ?? [];
        // Identity numbers are masked on screen (phones get lost); the salary certificate prints them in full.
        return new MyProfile(me.Id, me.EmpNo, me.NameEn, me.NameAr, me.JobTitleEn, me.JobTitleAr, department?.NameEn, department?.NameAr, me.Nationality,
            me.JoiningDate, me.Mobile, me.Email, HrEndpoints.Mask(me.Qid, false), HrEndpoints.Mask(me.PassportNo, false), me.BankShortName,
            HrEndpoints.Mask(me.Iban, false), salary, salary.Sum(s => s.Amount), contract?.AnnualLeaveDays);
    }

    private static async Task<MyLeave> LeaveAsync(ErpDbContext db, ITenantContext t, LeaveService leave, IClock clock, CancellationToken ct)
    {
        var me = await MeAsync(db, t, ct);
        var balance = await leave.BalanceAsync(me.Id, clock.Today, ct);
        var requests = await (from l in db.Set<LeaveRequest>().AsNoTracking()
                              join ty in db.Set<LeaveType>() on l.LeaveTypeId equals ty.Id
                              where l.EmployeeId == me.Id
                              orderby l.FromDate descending
                              select new MyLeaveRequest(l.Id, ty.Code, ty.NameEn, ty.NameAr, l.FromDate, l.ToDate, l.Days, l.Status.ToString(), l.Reason))
            .Take(100).ToListAsync(ct);
        var types = await db.Set<LeaveType>().AsNoTracking().Where(x => x.CompanyId == me.CompanyId).OrderBy(x => x.Code)
            .Select(x => new MyLeaveType(x.Code, x.NameEn, x.NameAr, x.IsPaid)).ToListAsync(ct);
        return new MyLeave(balance, requests, types);
    }
}

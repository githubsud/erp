using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Documents;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Reports;

/// <summary>Employee and company documents (QID, passport, licences…) expiring soon or already expired, for the AI assistant.</summary>
internal sealed class ExpiringDocumentsAiTool(DocumentService documents) : IAiTool
{
    public string Name => "expiring_documents";
    public string Description =>
        "Employee documents (QID, passport, work permit, health card…) and company documents (CR, licences) that expire within the given days or have expired.";
    public string Permission => HrPermissions.EmployeesRead;
    public string? Module => ModuleCodes.Hr;
    public string ParametersSchema => """{"type":"object","properties":{"within_days":{"type":"integer","description":"default 30"}}}""";

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var rows = await documents.ExpiringAsync(AiArgs.Int(args, "within_days", 30, 0, 365), ct);
        return new
        {
            expired = rows.Count(r => r.Status == ExpiryStatus.Expired),
            expiring = rows.Count(r => r.Status == ExpiryStatus.Expiring),
            documents = rows.OrderBy(r => r.ExpiryDate).Take(40).Select(r => new
            {
                owner = r.OwnerNameEn, owner_ar = r.OwnerNameAr, of = r.Scope, document = r.DocType, expiry_date = r.ExpiryDate, days_left = r.DaysLeft
            })
        };
    }
}

/// <summary>How many people work for the company, by nationality and department.</summary>
internal sealed class HeadcountAiTool(HrReportService reports) : IAiTool
{
    public string Name => "headcount";
    public string Description => "Number of active employees, Qatari share, joiners and leavers this year, and counts by nationality and by department.";
    public string Permission => HrPermissions.EmployeesRead;
    public string? Module => ModuleCodes.Hr;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var h = await reports.HeadcountAsync(null, ct);
        return new
        {
            h.AsOf, h.Total, h.Qatari, h.QatariPercent, h.JoinedThisYear, h.LeftThisYear,
            by_nationality = h.ByNationality.Select(c => new { nationality = c.LabelEn, nationality_ar = c.LabelAr, c.Count }),
            by_department = h.ByDepartment.Select(c => new { department = c.LabelEn, department_ar = c.LabelAr, c.Count })
        };
    }
}

/// <summary>Payroll totals per month (never per person), and the WPS deadline.</summary>
internal sealed class PayrollSummaryAiTool(ErpDbContext db, ITenantContext tenant) : IAiTool
{
    public string Name => "payroll_summary";
    public string Description => "Monthly payroll totals for the last months: employees paid, gross, deductions, net, status and the WPS payment deadline. Totals only.";
    public string Permission => HrPermissions.PayrollRead;
    public string? Module => ModuleCodes.Hr;
    public string ParametersSchema => """{"type":"object","properties":{"months":{"type":"integer","description":"how many recent months, default 3"}}}""";

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var months = AiArgs.Int(args, "months", 3, 1, 24);
        var runs = await db.Set<PayrollRun>().AsNoTracking()
            .Where(r => r.CompanyId == tenant.RequireCompanyId() && r.Status != PayrollStatus.Reversed)
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(months)
            .Select(r => new
            {
                month = r.Year + "-" + (r.Month < 10 ? "0" : "") + r.Month, status = r.Status.ToString(), employees = r.EmployeeCount, gross = r.TotalGross,
                deductions = r.TotalDeductions, net = r.TotalNet, wps_deadline = r.WpsDeadline
            }).ToListAsync(ct);
        return new { runs };
    }
}

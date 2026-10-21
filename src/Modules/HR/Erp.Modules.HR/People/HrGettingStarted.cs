using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.People;

/// <summary>The staff with their contracts and documents, then the first monthly payroll.</summary>
internal sealed class HrGettingStarted(ErpDbContext db, ITenantContext tenant) : IGettingStartedProvider
{
    public string Permission => HrPermissions.EmployeesManage;
    public string? Module => ModuleCodes.Hr;

    public async Task<IReadOnlyList<GettingStartedStep>> GetStepsAsync(CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var employees = await db.Set<Employee>().AnyAsync(e => e.CompanyId == companyId, ct);
        var payroll = await db.Set<PayrollRun>().AnyAsync(r => r.CompanyId == companyId, ct);
        return
        [
            new("employees", 70, "Add your employees", "أضف موظفيك",
                "With salary, QID and passport expiry dates; import them all from Excel.", "مع الراتب وتواريخ انتهاء البطاقة والجواز؛ استوردهم جميعاً من إكسل.",
                "/employees", employees),
            new("payroll", 80, "Run your first payroll", "أنشئ أول مسيرة رواتب",
                "Calculate the month, print payslips and make the WPS file for the bank.", "احسب الشهر، اطبع قسائم الرواتب وأنشئ ملف حماية الأجور للبنك.",
                "/payroll", payroll)
        ];
    }
}

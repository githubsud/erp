using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Purchasing.Endpoints;

/// <summary>Supplier aging as of today.</summary>
internal sealed class SupplierAgingScheduledReport(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance, IClock clock) : IScheduledReport
{
    public string Key => "pur.aging";
    public string NameEn => "Supplier aging";
    public string NameAr => "أعمار ذمم الموردين";
    public string Permission => PurchasingPermissions.ReportsRead;

    public async Task<ReportFile> RenderAsync(CancellationToken ct)
    {
        var r = await PurchasingEndpoints.AgingAsync(db, tenant, finance, ct, clock.Today);
        var content = CsvFile.Build(["Code", "Supplier", "المورد", "Current", "1-30", "31-60", "61-90", "Over 90", "Total"],
            r.Rows.Select(x => (IReadOnlyList<object?>)[x.Code, x.NameEn, x.NameAr, x.Current, x.Days1To30, x.Days31To60, x.Days61To90, x.Over90, x.Total]));
        var dueNow = r.Rows.Sum(x => x.Total - x.Current);
        return new ReportFile($"supplier_aging_{r.AsOf:yyyyMMdd}.csv", CsvFile.ContentType, content,
            $"Payables {r.Total:N2} to {r.Rows.Count} suppliers; past due: {dueNow:N2}.",
            $"الذمم الدائنة {r.Total:N2} لعدد {r.Rows.Count} مورد؛ المستحق المتأخر: {dueNow:N2}.");
    }
}

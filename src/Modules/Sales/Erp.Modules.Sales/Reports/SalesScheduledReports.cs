using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Sales.Endpoints;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Sales.Reports;

/// <summary>Customer aging as of today.</summary>
internal sealed class CustomerAgingScheduledReport(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance, IClock clock) : IScheduledReport
{
    public string Key => "sal.aging";
    public string NameEn => "Customer aging";
    public string NameAr => "أعمار ذمم العملاء";
    public string Permission => SalesPermissions.ReportsRead;

    public async Task<ReportFile> RenderAsync(CancellationToken ct)
    {
        var r = await SalesEndpoints.AgingAsync(db, tenant, finance, clock, ct);
        var content = CsvFile.Build(["Code", "Customer", "العميل", "Current", "1-30", "31-60", "61-90", "Over 90", "Total", "Credit limit"],
            r.Rows.Select(x => (IReadOnlyList<object?>)[x.Code, x.NameEn, x.NameAr, x.Current, x.Days1To30, x.Days31To60, x.Days61To90, x.Over90, x.Total,
                x.CreditLimit]));
        var overdue = r.Rows.Sum(x => x.Days31To60 + x.Days61To90 + x.Over90);
        return new ReportFile($"customer_aging_{r.AsOf:yyyyMMdd}.csv", CsvFile.ContentType, content,
            $"Receivables {r.Total:N2} from {r.Rows.Count} customers; over 30 days: {overdue:N2}.",
            $"الذمم المدينة {r.Total:N2} من {r.Rows.Count} عميل؛ المتأخر أكثر من 30 يوماً: {overdue:N2}.");
    }
}

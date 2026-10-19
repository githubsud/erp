using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Sales.Endpoints;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Reports;

/// <summary>Sales invoiced this month, receivables and what is more than 30 days late.</summary>
internal sealed class SalesDashboard(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance, IClock clock) : IDashboardProvider
{
    public string Permission => SalesPermissions.ReportsRead;
    public string? Module => ModuleCodes.Sales;

    public async Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var sales = await db.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.CompanyId == tenant.RequireCompanyId() && i.InvoiceDate >= monthStart && i.InvoiceDate <= today && i.Status != InvoiceStatus.Draft
                        && i.Status != InvoiceStatus.Reversed)
            .SumAsync(i => (decimal?)i.TotalBase, ct) ?? 0;
        var aging = await SalesEndpoints.AgingAsync(db, tenant, finance, clock, ct);
        var overdue = aging.Rows.Sum(r => r.Days31To60 + r.Days61To90 + r.Over90);
        return
        [
            new("sales", "sal.sales_mtd", "Sales this month", "مبيعات هذا الشهر", sales, "money", "/sales/invoices"),
            new("sales", "sal.receivables", "Receivables", "الذمم المدينة", aging.Total, "money", "/sales/reports"),
            new("sales", "sal.overdue", "Overdue more than 30 days", "متأخر أكثر من 30 يوماً", overdue, "money", "/sales/reports",
                Tone: overdue > 0 ? "warn" : "ok")
        ];
    }
}

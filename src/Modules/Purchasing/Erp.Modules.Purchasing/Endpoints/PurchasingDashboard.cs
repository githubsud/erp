using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Endpoints;

/// <summary>Payables and what falls due this week, orders waiting for approval, and items to reorder.</summary>
internal sealed class PurchasingDashboard(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance, IInventoryQueries inventory, IClock clock)
    : IDashboardProvider
{
    public string Permission => PurchasingPermissions.ReportsRead;

    public async Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var companyId = tenant.RequireCompanyId();
        var aging = await PurchasingEndpoints.AgingAsync(db, tenant, finance, ct, today);
        var week = today.AddDays(7);
        var dueThisWeek = await db.Set<SupplierInvoice>().AsNoTracking()
            .Where(i => i.CompanyId == companyId && (i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate <= week)
            .SumAsync(i => (decimal?)(i.TotalBase - i.RetentionAmount - i.TaxRetentionAmount - i.PaidBase - i.DebitedBase), ct) ?? 0;
        var pending = await db.Set<PurchaseOrder>().CountAsync(o => o.CompanyId == companyId && o.Status == PurchaseOrderStatus.PendingApproval, ct);
        var reorder = (await inventory.GetReorderCandidatesAsync(ct)).Count;
        return
        [
            new("purchasing", "pur.payables", "Payables", "الذمم الدائنة", aging.Total, "money", "/purchasing/reports"),
            new("purchasing", "pur.due_week", "Due within 7 days (incl. overdue)", "مستحق خلال 7 أيام (مع المتأخر)", dueThisWeek, "money", "/purchasing/payments",
                Tone: dueThisWeek > 0 ? "warn" : null),
            new("purchasing", "pur.pending_approval", "Orders waiting for approval", "أوامر بانتظار الاعتماد", pending, "count", "/purchasing/orders",
                Tone: pending > 0 ? "warn" : null),
            new("purchasing", "pur.reorder", "Items to reorder", "أصناف تحتاج إعادة طلب", reorder, "count", "/purchasing/reorder", Tone: reorder > 0 ? "warn" : "ok")
        ];
    }
}

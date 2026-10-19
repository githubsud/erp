using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Endpoints;

/// <summary>Stock value and batches expiring within 30 days (or already expired).</summary>
internal sealed class InventoryDashboard(ErpDbContext db, ITenantContext tenant, IClock clock) : IDashboardProvider
{
    public string Permission => InventoryPermissions.ReportsRead;
    public string? Module => ModuleCodes.Inventory;

    public async Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct)
    {
        var value = await (from v in db.Set<ItemValuation>().AsNoTracking()
                           join i in db.Set<Item>() on v.ItemId equals i.Id
                           where i.CompanyId == tenant.RequireCompanyId()
                           select (decimal?)v.Value).SumAsync(ct) ?? 0;
        var expiring = (await InventoryEndpoints.BatchesAsync(db, tenant, clock, null, null, 30, ct)).Count;
        return
        [
            new("inventory", "inv.value", "Stock value", "قيمة المخزون", value, "money", "/inventory/stock"),
            new("inventory", "inv.expiring", "Batches expiring in 30 days", "دفعات تنتهي خلال 30 يوماً", expiring, "count", "/inventory/expiry",
                Tone: expiring > 0 ? "warn" : "ok")
        ];
    }
}

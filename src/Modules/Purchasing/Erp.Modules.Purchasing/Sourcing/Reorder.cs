using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Documents;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Sourcing;

public sealed record ReorderSuggestion(Guid ItemId, string Sku, string NameEn, string NameAr, string UomCode, decimal OnHand, decimal OnOrder,
    decimal ReorderLevel, decimal SuggestedQuantity, Guid? SupplierId, string? SupplierNameEn, string? SupplierNameAr, decimal? UnitPrice);

public sealed record ReorderOrderLine(Guid ItemId, Guid SupplierId, decimal Quantity, decimal UnitPrice);

public sealed record ReorderOrdersRequest(Guid WarehouseId, DateOnly OrderDate, IReadOnlyList<ReorderOrderLine> Lines);

/// <summary>
/// Items at or below their reorder level, less what is already on order, with the supplier and price of the last order.
/// Suggested quantity: the item's reorder quantity, or enough to bring stock back to twice the reorder level.
/// </summary>
internal sealed class ReorderService(ErpDbContext db, ITenantContext tenant, IInventoryQueries inventory, PurchaseOrderService orders)
{
    private static readonly PurchaseOrderStatus[] Open = [PurchaseOrderStatus.Draft, PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Approved,
        PurchaseOrderStatus.PartiallyReceived];

    public async Task<List<ReorderSuggestion>> SuggestAsync(CancellationToken ct)
    {
        var candidates = await inventory.GetReorderCandidatesAsync(ct);
        var ids = candidates.Select(c => c.ItemId).ToList();
        var companyId = tenant.RequireCompanyId();
        var onOrder = await (from l in db.Set<PurchaseOrderLine>().AsNoTracking()
                             join o in db.Set<PurchaseOrder>() on l.PurchaseOrderId equals o.Id
                             where o.CompanyId == companyId && ids.Contains(l.ItemId) && Open.Contains(o.Status)
                             group l by l.ItemId into g
                             select new { g.Key, Quantity = g.Sum(x => x.Quantity - x.QuantityReceived) }).ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
        var last = (await (from l in db.Set<PurchaseOrderLine>().AsNoTracking()
                           join o in db.Set<PurchaseOrder>() on l.PurchaseOrderId equals o.Id
                           join s in db.Set<Supplier>() on o.SupplierId equals s.Id
                           where o.CompanyId == companyId && ids.Contains(l.ItemId) && o.Status != PurchaseOrderStatus.Cancelled && o.Currency == Money.BaseCurrency
                                 && s.IsActive
                           select new { l.ItemId, o.OrderDate, o.CreatedAt, s.Id, s.NameEn, s.NameAr, Price = l.UnitPrice }).ToListAsync(ct))
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.CreatedAt).First());
        var result = new List<ReorderSuggestion>();
        foreach (var c in candidates)
        {
            var ordered = onOrder.GetValueOrDefault(c.ItemId);
            if (c.OnHand + ordered > c.ReorderLevel) continue;
            var quantity = Math.Ceiling(c.ReorderQuantity ?? Math.Max(c.ReorderLevel * 2 - c.OnHand - ordered, 1));
            var supplier = last.GetValueOrDefault(c.ItemId);
            result.Add(new ReorderSuggestion(c.ItemId, c.Sku, c.NameEn, c.NameAr, c.UomCode, c.OnHand, ordered, c.ReorderLevel, quantity, supplier?.Id,
                supplier?.NameEn, supplier?.NameAr, supplier?.Price ?? c.PurchasePrice));
        }
        return result;
    }

    /// <summary>One draft order per supplier for the chosen lines.</summary>
    public Task<List<Guid>> CreateOrdersAsync(ReorderOrdersRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0))
                throw new BusinessRuleException("pur.no_lines", "Choose items with positive quantities.", "اختر أصنافاً بكميات موجبة.");
            var created = new List<Guid>();
            foreach (var g in r.Lines.GroupBy(l => l.SupplierId))
            {
                var order = await orders.CreateAsync(new OrderRequest(g.Key, r.OrderDate, null, r.WarehouseId, "Reorder suggestion / اقتراح إعادة الطلب",
                    g.Select(l => new OrderLineRequest(l.ItemId, null, l.Quantity, l.UnitPrice)).ToList()), token);
                created.Add(order.Id);
            }
            return created;
        }, ct);
}

internal static class ReorderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing/reorder-suggestions").WithTags("Sourcing").RequireAuthorization();
        g.MapGet("", (ReorderService s, CancellationToken ct) => s.SuggestAsync(ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/orders", async (ReorderOrdersRequest r, ReorderService s, CancellationToken ct) => new { PurchaseOrderIds = await s.CreateOrdersAsync(r, ct) })
            .RequirePermission(PurchasingPermissions.OrdersManage);
    }
}

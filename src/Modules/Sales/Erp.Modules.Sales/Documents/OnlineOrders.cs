using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

/// <summary>
/// Orders from the online store (Commerce) become ordinary sales orders on channel online, so delivery, invoicing, receipts and
/// posting are the existing ones.
/// </summary>
internal sealed class OnlineOrderIntake(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    CustomerService customers, IClock clock) : IOnlineOrderIntake
{
    public async Task<OnlineOrderResult> PlaceAsync(OnlineOrderRequest r, CancellationToken ct = default)
    {
        db.RequireTransaction("Placing an online order");
        var companyId = tenant.RequireCompanyId();
        var externalRef = CustomerService.Clean(r.ExternalRef) ?? throw new ArgumentException("An online order needs the store's order number.");
        if (await db.Set<SalesOrder>().AnyAsync(o => o.CompanyId == companyId && o.Channel == SalesChannel.Online && o.ExternalRef == externalRef, ct))
            throw new ConflictException("sal.online_order_exists", "This online order is already placed.", "تم تسجيل هذا الطلب الإلكتروني من قبل.");
        var warehouse = await inventory.GetWarehouseAsync(r.WarehouseId, ct);
        if (warehouse is not { IsActive: true }) throw new NotFoundException("Warehouse", r.WarehouseId);
        var customer = await WalkInCustomers.FindOrCreateAsync(db, tenant, customers, null, r.CustomerName, r.Phone, "WEB", ct);
        // Number only after validation and pricing pass (gapless numbering).
        var lines = await SalesLines.PriceAsync(customers, inventory, customer.Id,
            r.Lines.Select(l => new SalesLineRequest(l.ItemId, null, l.Quantity, l.UnitPrice, 0, l.UomId)).ToList(), ct);
        var order = new SalesOrder
        {
            CompanyId = companyId, SoNo = await numbering.NextAsync(SalesOrderService.SourceType, "SO", r.OrderDate, ct: ct), Channel = SalesChannel.Online,
            ExternalRef = externalRef, CustomerId = customer.Id, OrderDate = r.OrderDate, WarehouseId = r.WarehouseId, CustomerRef = externalRef,
            Notes = CustomerService.Clean(r.Notes), Status = SalesOrderStatus.Confirmed, ConfirmedAt = clock.UtcNow
        };
        db.Add(order);
        var lineNo = 0;
        foreach (var l in lines)
        {
            var line = new SalesOrderLine
            {
                SalesOrderId = order.Id, LineNo = ++lineNo, ItemId = l.ItemId, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent, Amount = l.Amount, UomId = l.UomId, UomFactor = l.UomFactor, UomQuantity = l.UomQuantity, UomPrice = l.UomPrice
            };
            order.Lines.Add(line);
            db.Add(line);
        }
        order.Total = order.Lines.Sum(l => l.Amount);
        await db.SaveChangesAsync(ct);
        return new OnlineOrderResult(order.Id, order.SoNo, customer.Id, order.Total);
    }

    public async Task<IReadOnlyDictionary<Guid, OnlineOrderStatus>> GetStatusAsync(IEnumerable<Guid> salesOrderIds, CancellationToken ct = default)
    {
        var ids = salesOrderIds.Distinct().ToList();
        var orders = await db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines)
            .Where(o => o.CompanyId == tenant.RequireCompanyId() && ids.Contains(o.Id)).ToListAsync(ct);
        var items = await inventory.GetItemsAsync(orders.SelectMany(o => o.Lines.Select(l => l.ItemId)), ct);
        var lineIds = orders.SelectMany(o => o.Lines.Select(l => l.Id)).ToList();
        var open = await (from i in db.Set<SalesInvoice>().AsNoTracking()
                          where i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid
                          where db.Set<SalesInvoiceLine>().Any(x => x.SalesInvoiceId == i.Id && x.SalesOrderLineId != null && lineIds.Contains(x.SalesOrderLineId.Value))
                          select new
                          {
                              i.Id, i.Total, i.PaidAmount, i.CreditedAmount,
                              OrderLineIds = db.Set<SalesInvoiceLine>().Where(x => x.SalesInvoiceId == i.Id && x.SalesOrderLineId != null).Select(x => x.SalesOrderLineId!.Value).ToList()
                          }).ToListAsync(ct);
        return orders.ToDictionary(o => o.Id, o =>
        {
            var mine = o.Lines.Select(l => l.Id).ToHashSet();
            var stock = o.Lines.Where(l => items.TryGetValue(l.ItemId, out var i) && i.Type == ItemType.Stock).ToList();
            return new OnlineOrderStatus(o.Id, o.SoNo, o.Status.ToString(), o.CustomerId, o.Total, stock.All(l => l.QuantityDelivered >= l.Quantity),
                o.Lines.All(l => l.QuantityInvoiced >= l.Quantity),
                open.Where(i => i.OrderLineIds.Any(mine.Contains)).Sum(i => i.Total - i.PaidAmount - i.CreditedAmount));
        });
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetUndeliveredQuantitiesAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default)
    {
        var ids = (await inventory.GetItemsAsync(itemIds.Distinct(), ct)).Values.Where(i => i.Type == ItemType.Stock).Select(i => i.Id).ToList();
        var rows = await (from l in db.Set<SalesOrderLine>().AsNoTracking()
                          join o in db.Set<SalesOrder>() on l.SalesOrderId equals o.Id
                          where o.CompanyId == tenant.RequireCompanyId() && ids.Contains(l.ItemId) && l.Quantity > l.QuantityDelivered
                                && (o.Status == SalesOrderStatus.Confirmed || o.Status == SalesOrderStatus.PartiallyDelivered || o.Status == SalesOrderStatus.CreditHold)
                          group l by l.ItemId into g
                          select new { ItemId = g.Key, Quantity = g.Sum(x => x.Quantity - x.QuantityDelivered) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.ItemId, r => r.Quantity);
    }
}

using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Sales.Documents;

/// <summary>The same pricing as quotations and sales orders (<see cref="SalesLines.PriceAsync"/>), for other modules.</summary>
internal sealed class SalesPricing(ErpDbContext db, ITenantContext tenant, CustomerService customers, IInventoryQueries inventory) : ISalesPricing
{
    public async Task<PricedBasket> PriceAsync(Guid? customerId, IReadOnlyList<BasketLine> lines, CancellationToken ct = default)
    {
        if (customerId is { } id)
            await CustomerService.ActiveCustomerAsync(db, tenant, id, ct);
        var priced = await SalesLines.PriceAsync(customers, inventory, customerId,
            lines.Select(l => new SalesLineRequest(l.ItemId, null, l.Quantity, UomId: l.UomId)).ToList(), ct);
        var result = priced.Select(l => new PricedBasketLine(l.ItemId, l.UomId, l.UomQuantity, l.UomPrice, l.Amount, l.UomFactor, l.Quantity)).ToList();
        return new PricedBasket(result, result.Sum(l => l.Amount));
    }
}

namespace Erp.Modules.Sales.Contracts;

/// <summary>A basket line in <see cref="UomId"/> (an alternative unit of the item, e.g. a box) or, without it, the item's base unit.</summary>
public sealed record BasketLine(Guid ItemId, decimal Quantity, Guid? UomId = null);

/// <summary>
/// <see cref="Quantity"/> and <see cref="UnitPrice"/> per the entered unit; <see cref="BaseQuantity"/> = quantity × <see cref="UomFactor"/>
/// in the item's base unit (what stock counts).
/// </summary>
public sealed record PricedBasketLine(Guid ItemId, Guid? UomId, decimal Quantity, decimal UnitPrice, decimal Amount, decimal UomFactor, decimal BaseQuantity);

/// <summary>Amounts in QAR. Qatar has no VAT, so there is no tax line.</summary>
public sealed record PricedBasket(IReadOnlyList<PricedBasketLine> Lines, decimal Total);

/// <summary>
/// Prices a basket exactly as a quotation or sales order would for the same customer: the customer's price list, then the item's (or
/// the unit's) sales price. Without a customer (a guest) the item's sales price is used, which is also what the POS charges.
/// Runs in the caller's company (<see cref="Erp.SharedKernel.Application.ITenantContext"/>).
/// </summary>
public interface ISalesPricing
{
    Task<PricedBasket> PriceAsync(Guid? customerId, IReadOnlyList<BasketLine> lines, CancellationToken ct = default);
}

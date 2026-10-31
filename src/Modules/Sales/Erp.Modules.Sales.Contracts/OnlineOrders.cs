namespace Erp.Modules.Sales.Contracts;

/// <summary>A line of an online order. <see cref="UnitPrice"/> is set only for charges the store fixes itself (the delivery fee).</summary>
public sealed record OnlineOrderLine(Guid ItemId, decimal Quantity, Guid? UomId = null, decimal? UnitPrice = null);

/// <summary>
/// The shopper is found by phone among the company's customers, or created as a cash customer. <see cref="ExternalRef"/> is the
/// store's order number, unique per company.
/// </summary>
public sealed record OnlineOrderRequest(string CustomerName, string Phone, Guid WarehouseId, string ExternalRef, DateOnly OrderDate, string? Notes,
    IReadOnlyList<OnlineOrderLine> Lines);

public sealed record OnlineOrderResult(Guid SalesOrderId, string SoNo, Guid CustomerId, decimal Total);

/// <summary>Where an online order stands in Sales: delivered, invoiced and what is still to be collected.</summary>
public sealed record OnlineOrderStatus(Guid SalesOrderId, string SoNo, string Status, Guid CustomerId, decimal Total, bool FullyDelivered,
    bool FullyInvoiced, decimal Outstanding);

/// <summary>Online orders as Sales documents (design §2.1, §5.3).</summary>
public interface IOnlineOrderIntake
{
    /// <summary>
    /// Creates and confirms a sales order (channel online, gapless number) inside the caller's transaction. Cash on delivery is not
    /// credit, so no credit check holds it.
    /// </summary>
    Task<OnlineOrderResult> PlaceAsync(OnlineOrderRequest request, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, OnlineOrderStatus>> GetStatusAsync(IEnumerable<Guid> salesOrderIds, CancellationToken ct = default);

    /// <summary>Base-unit quantities still to be delivered on open sales orders of any channel: stock that is promised but still on hand.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetUndeliveredQuantitiesAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default);
}

namespace Erp.Modules.Commerce.Contracts;

/// <summary>Outbox message types published by the online store.</summary>
public static class CommerceEvents
{
    public const string OnlineOrderPlaced = "commerce.online_order_placed";
}

/// <summary>A shopper placed an order; it is a confirmed sales order (<see cref="SalesOrderId"/>) from here on.</summary>
public sealed record OnlineOrderPlaced(Guid CompanyId, Guid StoreId, Guid CheckoutId, string OrderNo, Guid SalesOrderId, decimal Total, string PaymentMethod);

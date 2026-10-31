using System.Security.Cryptography;
using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Commerce.Contracts;
using Erp.Modules.Commerce.Domain;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Commerce.Storefront;

/// <summary>Cash on delivery to a Qatar address: zone, street and building numbers from the blue plate, an optional unit and notes.</summary>
public sealed record CheckoutRequest(string CustomerName, string Phone, string Zone, string Street, string Building, string? Unit, string? Notes,
    decimal? Latitude = null, decimal? Longitude = null);

/// <summary><see cref="StatusToken"/> lets the shopper follow the order (header <c>X-Order-Token</c>); it is shown once.</summary>
public sealed record PlacedOrderDto(string OrderNo, string StatusToken, decimal Total);

public sealed record OrderLineDto(string TitleEn, string TitleAr, string UomCode, decimal Quantity, decimal UnitPrice, decimal Amount);

/// <summary>Received, delivered or cancelled, with what was ordered.</summary>
public sealed record OrderStatusDto(string OrderNo, string Status, DateTimeOffset PlacedAt, IReadOnlyList<OrderLineDto> Lines, decimal Subtotal,
    decimal DeliveryFee, decimal Total, string PaymentMethod);

internal sealed class CheckoutService(ErpDbContext db, ITenantContext tenant, CartService carts, StockAvailability availability,
    IOnlineOrderIntake orders, INumberingService numbering, IOutbox outbox, IClock clock)
{
    public const string OrderTokenHeader = "X-Order-Token";
    public const string SourceType = "online_order";
    private static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Places the cart as a cash-on-delivery order in one transaction (design §5.2–5.3): the products are locked so two shoppers racing
    /// for the last unit are served one after the other, the cart is re-priced, stock is checked and held, and the sales order, the
    /// checkout, the converted holds and the OnlineOrderPlaced message commit together.
    /// </summary>
    public Task<PlacedOrderDto> PlaceCodAsync(Store store, HttpContext http, CheckoutRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var name = Required(r.CustomerName, 200, "name");
            var phone = Required(r.Phone, 40, "phone");
            if (phone.Count(char.IsDigit) is < 8 or > 15)
                throw new BusinessRuleException("commerce.phone_invalid", "Enter a mobile number of at least 8 digits.", "أدخل رقم جوال من 8 أرقام على الأقل.");
            var zone = Required(r.Zone, 10, "zone");
            var street = Required(r.Street, 10, "street");
            var building = Required(r.Building, 10, "building");
            if (r.Latitude is < -90 or > 90 || r.Longitude is < -180 or > 180)
                throw new BusinessRuleException("commerce.location_invalid", "The map location is not valid.", "الموقع على الخريطة غير صحيح.");
            if (!store.CodEnabled || store.WarehouseId is not { } warehouseId || (store.DeliveryFee > 0 && store.DeliveryItemId is null))
                throw new BusinessRuleException("commerce.checkout_closed", "This store is not taking orders yet.", "هذا المتجر لا يستقبل الطلبات بعد.");

            var cart = await carts.FindAsync(store, http, token);
            if (cart.Lines.Count == 0)
                throw new BusinessRuleException("commerce.cart_empty", "Your cart is empty.", "سلتك فارغة.");
            // Shoppers buying the same products queue here; a till sale does not take this lock (the hold is soft, design §0).
            var listingIds = cart.Lines.Select(l => l.ListingId).Distinct().OrderBy(id => id).ToArray();
            var tenantId = tenant.RequireTenantId();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM commerce.listing WHERE tenant_id = {tenantId} AND id = ANY({listingIds}) ORDER BY id FOR UPDATE", token);

            var (lines, subtotal) = await carts.PriceAsync(store, cart, token);
            if (lines.Any(l => !l.Available))
                throw new BusinessRuleException("commerce.cart_changed", "Some products in your cart are no longer sold. Please review your cart.",
                    "بعض المنتجات في سلتك لم تعد متاحة. يرجى مراجعة السلة.");
            if (subtotal < store.MinOrder)
                throw new BusinessRuleException("commerce.below_minimum", $"The minimum order is {store.MinOrder:0.##} QAR.",
                    $"الحد الأدنى للطلب {store.MinOrder:0.##} ريال.");
            var wanted = lines.Where(l => l.Item!.Type == ItemType.Stock).GroupBy(l => l.Line.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQuantity));
            var free = await availability.AvailableAsync(lines.Select(l => l.Item!).DistinctBy(i => i.Id), token, cart.Id);
            var short_ = lines.Where(l => wanted.TryGetValue(l.Line.ItemId, out var q) && free.GetValueOrDefault(l.Line.ItemId) < q).Select(l => l.Listing!.TitleEn)
                .Distinct().ToList();
            if (short_.Count > 0)
                throw new ConflictException("commerce.out_of_stock", $"Not enough stock for: {string.Join(", ", short_)}.",
                    $"الكمية غير متوفرة لـ: {string.Join("، ", lines.Where(l => short_.Contains(l.Listing!.TitleEn)).Select(l => l.Listing!.TitleAr).Distinct())}.");

            var now = clock.UtcNow;
            var holds = wanted.Select(w => new Reservation
            {
                StoreId = store.Id, CartId = cart.Id, ItemId = w.Key, WarehouseId = warehouseId, Quantity = w.Value, ExpiresAt = now + HoldFor
            }).ToList();
            db.AddRange(holds);
            await db.SaveChangesAsync(token);

            var fee = store.DeliveryFee;
            var orderNo = await numbering.NextAsync(SourceType, "WEB", clock.Today, ct: token);
            var orderLines = lines.Select(l => new OnlineOrderLine(l.Line.ItemId, l.Line.Quantity, l.Line.UomId, l.UnitPrice)).ToList();
            if (fee > 0) orderLines.Add(new OnlineOrderLine(store.DeliveryItemId!.Value, 1m, null, fee));
            var address = $"Zone {zone}, Street {street}, Building {building}" + (string.IsNullOrWhiteSpace(r.Unit) ? "" : $", Unit {r.Unit.Trim()}");
            var placed = await orders.PlaceAsync(new OnlineOrderRequest(name, phone, warehouseId, orderNo, clock.Today,
                Clean(r.Notes, 500) is { } notes ? $"{address}. {notes}" : address, orderLines), token);

            var statusToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var checkout = new Checkout
            {
                CompanyId = store.CompanyId, StoreId = store.Id, CartId = cart.Id, OrderNo = orderNo, CustomerName = name, Phone = phone, Zone = zone,
                Street = street, Building = building, Unit = Clean(r.Unit, 20), AddressNotes = Clean(r.Notes, 500), Latitude = r.Latitude,
                Longitude = r.Longitude, Subtotal = subtotal, DeliveryFee = fee, Total = subtotal + fee, SalesOrderId = placed.SalesOrderId,
                StatusTokenHash = CartService.Hash(statusToken),
                PricedSnapshot = JsonSerializer.Serialize(lines.Select(l => new OrderLineDto(l.Listing!.TitleEn, l.Listing.TitleAr, l.UomCode, l.Line.Quantity,
                    l.UnitPrice, l.Amount)), JsonSerializerOptions.Web)
            };
            db.Add(checkout);
            foreach (var hold in holds)
            {
                hold.Status = ReservationStatus.Converted;
                hold.CheckoutId = checkout.Id;
            }
            cart.Status = CartStatus.CheckedOut;
            outbox.Enqueue(CommerceEvents.OnlineOrderPlaced, new OnlineOrderPlaced(store.CompanyId, store.Id, checkout.Id, orderNo, placed.SalesOrderId,
                checkout.Total, "cod"));
            await db.SaveChangesAsync(token);
            return new PlacedOrderDto(orderNo, statusToken, checkout.Total);
        }, ct);

    /// <summary>The shopper's view of an order, for the status key given at checkout only.</summary>
    public async Task<OrderStatusDto> StatusAsync(Store store, string orderNo, HttpContext http, CancellationToken ct)
    {
        var token = http.Request.Headers[OrderTokenHeader].ToString().Trim();
        var hash = token.Length is 0 or > 100 ? "" : CartService.Hash(token);
        var checkout = await db.Set<Checkout>().AsNoTracking()
                           .SingleOrDefaultAsync(c => c.StoreId == store.Id && c.OrderNo == orderNo && c.StatusTokenHash == hash, ct)
                       ?? throw new NotFoundException("Order", orderNo);
        var status = (await orders.GetStatusAsync([checkout.SalesOrderId], ct)).GetValueOrDefault(checkout.SalesOrderId);
        var state = checkout.Status == CheckoutStatus.Cancelled || status?.Status is "Cancelled" ? "Cancelled"
            : status is { FullyDelivered: true } && status.Status is "Delivered" or "Closed" ? "Delivered"
            : "Received";
        var lines = JsonSerializer.Deserialize<List<OrderLineDto>>(checkout.PricedSnapshot, JsonSerializerOptions.Web) ?? [];
        return new OrderStatusDto(checkout.OrderNo, state, checkout.CreatedAt, lines, checkout.Subtotal, checkout.DeliveryFee, checkout.Total, "Cod");
    }

    private static string Required(string? value, int max, string field) =>
        Clean(value, max) ?? throw new BusinessRuleException("commerce.checkout_field", $"Enter your {field}.", field switch
        {
            "name" => "أدخل اسمك.",
            "phone" => "أدخل رقم الجوال.",
            "zone" => "أدخل رقم المنطقة.",
            "street" => "أدخل رقم الشارع.",
            _ => "أدخل رقم المبنى."
        });

    private static string? Clean(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }
}

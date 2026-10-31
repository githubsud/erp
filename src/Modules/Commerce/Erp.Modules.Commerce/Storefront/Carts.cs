using System.Security.Cryptography;
using System.Text;
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

/// <summary>Product by its store address; the unit by its code (a pack such as BOX12), the base unit when empty.</summary>
public sealed record CartLineRequest(string ProductSlug, string? UomCode, decimal Quantity);

/// <summary><see cref="Enough"/>: the store can supply this quantity now (the exact stock is never shown).</summary>
public sealed record CartLineDto(string ProductSlug, string TitleEn, string TitleAr, string UomCode, decimal Quantity, decimal UnitPrice, decimal Amount,
    AvailabilityBand Availability, bool Enough, bool Available);

public sealed record CartDto(IReadOnlyList<CartLineDto> Lines, decimal Subtotal, decimal DeliveryFee, decimal Total, decimal MinOrder, bool MeetsMinimum,
    bool CanCheckout);

public sealed record NewCartDto(string Token, CartDto Cart);

/// <summary>A priced cart line with what checkout needs.</summary>
internal sealed record PricedCartLine(CartLine Line, Listing? Listing, CatalogItem? Item, string UomCode, decimal UnitPrice, decimal Amount, decimal BaseQuantity,
    bool Available);

/// <summary>
/// Stock the store can still promise: on hand, less what open sales orders still have to deliver (any channel), less other carts'
/// active holds.
/// </summary>
internal sealed class StockAvailability(ErpDbContext db, IOnlineOrderIntake orders, IClock clock)
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> AvailableAsync(IEnumerable<CatalogItem> items, CancellationToken ct, Guid? exceptCart = null)
    {
        var stock = items.Where(i => i.Type == ItemType.Stock).ToList();
        var ids = stock.Select(i => i.Id).ToList();
        var promised = await orders.GetUndeliveredQuantitiesAsync(ids, ct);
        var now = clock.UtcNow;
        var held = await db.Set<Reservation>().AsNoTracking()
            .Where(r => ids.Contains(r.ItemId) && r.Status == ReservationStatus.Held && r.ExpiresAt > now && r.CartId != exceptCart)
            .GroupBy(r => r.ItemId).Select(g => new { ItemId = g.Key, Quantity = g.Sum(r => r.Quantity) }).ToDictionaryAsync(x => x.ItemId, x => x.Quantity, ct);
        return stock.ToDictionary(i => i.Id, i => (i.OnHand ?? 0m) - promised.GetValueOrDefault(i.Id) - held.GetValueOrDefault(i.Id));
    }
}

/// <summary>Anonymous carts of the current store. The cart key travels in the <c>X-Cart-Token</c> header.</summary>
internal sealed class CartService(ErpDbContext db, IInventoryQueries inventory, ISalesPricing pricing, StockAvailability availability, IClock clock)
{
    public const string TokenHeader = "X-Cart-Token";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(string Token, Cart Cart)> CreateAsync(Store store, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var cart = new Cart { StoreId = store.Id, TokenHash = Hash(token), ExpiresAt = clock.UtcNow + Lifetime };
        db.Add(cart);
        await db.SaveChangesAsync(ct);
        return (token, cart);
    }

    /// <summary>The open cart of this store for the request's key; an unknown, expired or used key is 404.</summary>
    public async Task<Cart> FindAsync(Store store, HttpContext http, CancellationToken ct)
    {
        var token = http.Request.Headers[TokenHeader].ToString();
        var hash = string.IsNullOrWhiteSpace(token) || token.Length > 100 ? "" : Hash(token.Trim());
        var now = clock.UtcNow;
        return await db.Set<Cart>().Include(c => c.Lines)
                   .SingleOrDefaultAsync(c => c.TokenHash == hash && c.StoreId == store.Id && c.Status == CartStatus.Open && c.ExpiresAt > now, ct)
               ?? throw new NotFoundException("Cart", "token");
    }

    public async Task<Cart> SetLineAsync(Store store, HttpContext http, CartLineRequest r, CancellationToken ct)
    {
        var cart = await FindAsync(store, http, ct);
        if (r.Quantity < 0 || r.Quantity > 10_000 || decimal.Round(r.Quantity, 3) != r.Quantity)
            throw new BusinessRuleException("commerce.cart_quantity", "Enter a quantity between 0 and 10,000.", "أدخل كمية بين 0 و10,000.");
        var slug = (r.ProductSlug ?? "").Trim().ToLowerInvariant();
        var listing = await db.Set<Listing>().AsNoTracking().SingleOrDefaultAsync(l => l.StoreId == store.Id && l.Slug == slug && l.IsPublished, ct)
                      ?? throw new NotFoundException("Product", slug);
        var item = (await inventory.GetCatalogAsync(ct)).SingleOrDefault(i => i.Id == listing.ItemId) ?? throw new NotFoundException("Product", slug);
        Guid? uomId = null;
        if (!string.IsNullOrWhiteSpace(r.UomCode) && !string.Equals(r.UomCode.Trim(), item.UomCode, StringComparison.OrdinalIgnoreCase))
            uomId = (item.Units ?? []).SingleOrDefault(u => string.Equals(u.UomCode, r.UomCode.Trim(), StringComparison.OrdinalIgnoreCase))?.UomId
                    ?? throw new NotFoundException("Unit", r.UomCode);
        var line = cart.Lines.SingleOrDefault(l => l.ItemId == item.Id && l.UomId == uomId);
        if (r.Quantity == 0)
        {
            if (line is not null)
            {
                cart.Lines.Remove(line);
                db.Remove(line);
            }
        }
        else if (line is null)
        {
            line = new CartLine { CartId = cart.Id, ListingId = listing.Id, ItemId = item.Id, UomId = uomId, Quantity = r.Quantity };
            cart.Lines.Add(line);
            db.Add(line);
        }
        else line.Quantity = r.Quantity;
        if (cart.Lines.Count > 100)
            throw new BusinessRuleException("commerce.cart_full", "A cart holds up to 100 products.", "تتسع السلة لـ100 منتج كحد أقصى.");
        cart.ExpiresAt = clock.UtcNow + Lifetime;
        await db.SaveChangesAsync(ct);
        return cart;
    }

    /// <summary>
    /// Re-prices the cart as a guest through Sales. Lines whose product was hidden, deactivated or lost its unit stay in the cart but
    /// are not available and not counted.
    /// </summary>
    public async Task<(IReadOnlyList<PricedCartLine> Lines, decimal Subtotal)> PriceAsync(Store store, Cart cart, CancellationToken ct)
    {
        var listingIds = cart.Lines.Select(l => l.ListingId).ToList();
        var listings = await db.Set<Listing>().AsNoTracking().Where(l => listingIds.Contains(l.Id) && l.StoreId == store.Id).ToDictionaryAsync(l => l.Id, ct);
        var catalog = (await inventory.GetCatalogAsync(ct)).ToDictionary(i => i.Id);
        bool Sellable(CartLine l) =>
            listings.TryGetValue(l.ListingId, out var listing) && listing.IsPublished && catalog.TryGetValue(l.ItemId, out var item)
            && (l.UomId is null || (item.Units ?? []).Any(u => u.UomId == l.UomId));
        var sellable = cart.Lines.Where(Sellable).OrderBy(l => l.CreatedAt).ToList();
        var priced = sellable.Count == 0 ? [] : (await pricing.PriceAsync(null, sellable.Select(l => new BasketLine(l.ItemId, l.Quantity, l.UomId)).ToList(), ct)).Lines;
        var result = cart.Lines.OrderBy(l => l.CreatedAt).Select(l =>
        {
            var index = sellable.IndexOf(l);
            var item = catalog.GetValueOrDefault(l.ItemId);
            var uomCode = l.UomId is { } u ? (item?.Units ?? []).FirstOrDefault(x => x.UomId == u)?.UomCode ?? "" : item?.UomCode ?? "";
            return index < 0
                ? new PricedCartLine(l, listings.GetValueOrDefault(l.ListingId), item, uomCode, 0m, 0m, 0m, false)
                : new PricedCartLine(l, listings[l.ListingId], item, uomCode, priced[index].UnitPrice, priced[index].Amount, priced[index].BaseQuantity, true);
        }).ToList();
        return (result, result.Where(l => l.Available).Sum(l => l.Amount));
    }

    public async Task<CartDto> ToDtoAsync(Store store, Cart cart, CancellationToken ct)
    {
        var (lines, subtotal) = await PriceAsync(store, cart, ct);
        var available = await availability.AvailableAsync(lines.Where(l => l.Item is not null).Select(l => l.Item!).DistinctBy(i => i.Id), ct, cart.Id);
        var wanted = lines.Where(l => l.Available).GroupBy(l => l.Line.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQuantity));
        var dtos = lines.Select(l =>
        {
            var stocked = l.Item is { Type: ItemType.Stock };
            var free = stocked ? available.GetValueOrDefault(l.Line.ItemId) : (decimal?)null;
            var enough = !stocked || free >= wanted.GetValueOrDefault(l.Line.ItemId);
            return new CartLineDto(l.Listing?.Slug ?? "", l.Listing?.TitleEn ?? "", l.Listing?.TitleAr ?? "", l.UomCode, l.Line.Quantity, l.UnitPrice, l.Amount,
                l.Item is null ? AvailabilityBand.OutOfStock : Availability.Band(free, l.Item.ReorderLevel, stocked), enough && l.Available, l.Available);
        }).ToList();
        var fee = dtos.Any(l => l.Available) ? store.DeliveryFee : 0m;
        var meetsMinimum = subtotal >= store.MinOrder;
        return new CartDto(dtos, subtotal, fee, subtotal + fee, store.MinOrder, meetsMinimum,
            store.CodEnabled && store.WarehouseId is not null && (fee == 0 || store.DeliveryItemId is not null) && meetsMinimum && dtos.Count > 0 && dtos.All(l => l.Available && l.Enough));
    }
}

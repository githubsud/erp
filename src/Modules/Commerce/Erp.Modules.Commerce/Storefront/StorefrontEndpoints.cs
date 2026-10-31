using Erp.Infrastructure.Persistence;
using Erp.Modules.Commerce.Contracts;
using Erp.Modules.Commerce.Domain;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Commerce.Storefront;

// Public responses are a whitelist (design §3.4): store profile, published listings, guest prices and availability bands. Never cost,
// supplier, exact stock, item ids or anything about other customers.
public sealed record PublicStoreDto(string Slug, string NameEn, string NameAr, string? Phone, string Currency, bool CodEnabled, decimal MinOrder,
    decimal DeliveryFee);

public sealed record PublicCategoryDto(string Slug, string NameEn, string NameAr, string? ParentSlug, int SortOrder);

/// <summary>A way the product is sold: the base unit (<see cref="Factor"/> 1) or a pack such as a box of 12.</summary>
public sealed record PublicUnitDto(string UomCode, decimal Factor, decimal Price);

public sealed record PublicProductDto(string Slug, string TitleEn, string TitleAr, string? CategorySlug, decimal Price, string UomCode,
    IReadOnlyList<PublicUnitDto> Units, AvailabilityBand Availability, IReadOnlyList<string> Images, string? DescriptionEn = null,
    string? DescriptionAr = null);

/// <summary>Published listings of the current store, priced as a guest through Sales and shown with availability bands.</summary>
internal sealed class StorefrontCatalog(ErpDbContext db, IInventoryQueries inventory, ISalesPricing pricing)
{
    public async Task<IReadOnlyList<PublicProductDto>> ProductsAsync(Store store, string? categorySlug, string? search, string? productSlug,
        bool withDescription, CancellationToken ct)
    {
        var categories = await db.Set<Category>().AsNoTracking().Where(c => c.StoreId == store.Id && c.IsActive).ToListAsync(ct);
        var listings = await db.Set<Listing>().AsNoTracking()
            .Where(l => l.StoreId == store.Id && l.IsPublished && (productSlug == null || l.Slug == productSlug))
            .OrderBy(l => l.SortOrder).ThenBy(l => l.TitleEn).ToListAsync(ct);
        // Only active items with a sales price can be sold, whatever the listing says.
        var catalog = (await inventory.GetCatalogAsync(ct)).ToDictionary(i => i.Id);
        listings = listings.Where(l => catalog.ContainsKey(l.ItemId)).ToList();
        var items = await inventory.GetItemsAsync(listings.Select(l => l.ItemId), ct);

        if (categorySlug is not null)
        {
            var category = categories.SingleOrDefault(c => c.Slug == categorySlug);
            if (category is null) return [];
            listings = listings.Where(l => l.CategoryId == category.Id
                                           || l.CategoryId is null && category.ItemGroupId is { } g && items.TryGetValue(l.ItemId, out var i) && i.ItemGroupId == g).ToList();
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            listings = listings.Where(l => l.TitleEn.Contains(term, StringComparison.OrdinalIgnoreCase) || l.TitleAr.Contains(term, StringComparison.Ordinal)).ToList();
        }
        if (listings.Count == 0) return [];

        // One guest basket prices every product in its base unit and in each of its packs.
        var basket = listings.SelectMany(l => new[] { new BasketLine(l.ItemId, 1m) }
            .Concat((catalog[l.ItemId].Units ?? []).Select(u => new BasketLine(l.ItemId, 1m, u.UomId)))).ToList();
        var prices = (await pricing.PriceAsync(null, basket, ct)).Lines.ToDictionary(p => (p.ItemId, p.UomId), p => p.UnitPrice);

        var listingIds = listings.Select(l => l.Id).ToList();
        var images = (await db.Set<ListingImage>().AsNoTracking().Where(x => listingIds.Contains(x.ListingId)).OrderBy(x => x.SortOrder).ToListAsync(ct))
            .ToLookup(x => x.ListingId, x => x.StorageKey);
        string? CategoryOf(Listing l) =>
            (l.CategoryId is { } id ? categories.FirstOrDefault(c => c.Id == id)
                : categories.FirstOrDefault(c => c.ItemGroupId is { } g && items.TryGetValue(l.ItemId, out var i) && i.ItemGroupId == g))?.Slug;

        return listings.Select(l =>
        {
            var item = catalog[l.ItemId];
            var units = new[] { new PublicUnitDto(item.UomCode, 1m, prices[(l.ItemId, null)]) }
                .Concat((item.Units ?? []).Select(u => new PublicUnitDto(u.UomCode, u.Factor, prices[(l.ItemId, u.UomId)]))).ToList();
            return new PublicProductDto(l.Slug, l.TitleEn, l.TitleAr, CategoryOf(l), units[0].Price, item.UomCode, units,
                Availability.Band(item.OnHand, item.ReorderLevel, item.Type == ItemType.Stock), images[l.Id].ToList(),
                withDescription ? l.DescriptionEn : null, withDescription ? l.DescriptionAr : null);
        }).ToList();
    }
}

internal static class StorefrontEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/store/{slug}").WithTags("Storefront").AllowAnonymous().AddEndpointFilter(StorefrontTenant.ResolveAsync);

        g.MapGet("", (HttpContext http) =>
        {
            var s = StorefrontTenant.Current(http);
            return new PublicStoreDto(s.Slug, s.NameEn, s.NameAr, s.Phone, s.Currency, s.CodEnabled, s.MinOrder, s.DeliveryFee);
        });

        g.MapGet("/categories", async (HttpContext http, ErpDbContext db, CancellationToken ct) =>
        {
            var store = StorefrontTenant.Current(http);
            var categories = await db.Set<Category>().AsNoTracking().Where(c => c.StoreId == store.Id && c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync(ct);
            var slugs = categories.ToDictionary(c => c.Id, c => c.Slug);
            return categories.Select(c => new PublicCategoryDto(c.Slug, c.NameEn, c.NameAr, c.ParentId is { } p ? slugs.GetValueOrDefault(p) : null, c.SortOrder));
        });

        g.MapGet("/catalog", async (HttpContext http, StorefrontCatalog catalog, CancellationToken ct, string? category = null, string? search = null,
            int page = 1, int pageSize = 24) =>
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var products = await catalog.ProductsAsync(StorefrontTenant.Current(http), category, search, null, false, ct);
            return new PagedResult<PublicProductDto>(products.Skip((page - 1) * pageSize).Take(pageSize).ToList(), products.Count, page, pageSize);
        });

        g.MapGet("/products/{productSlug}", async (HttpContext http, string productSlug, StorefrontCatalog catalog, CancellationToken ct) =>
            (await catalog.ProductsAsync(StorefrontTenant.Current(http), null, null, productSlug.Trim().ToLowerInvariant(), true, ct)).SingleOrDefault()
            ?? throw new NotFoundException("Product", productSlug));
    }
}

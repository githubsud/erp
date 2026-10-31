using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Commerce.Domain;
using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Commerce.Admin;

/// <summary>
/// Host: a custom domain such as shop.example.qa (optional). Status: draft, open or closed. Warehouse: where online orders are sent
/// from. Delivery item: the service item the delivery fee is charged as.
/// </summary>
public sealed record StoreRequest(string Slug, string NameEn, string NameAr, string? Phone, string? Host, bool CodEnabled, decimal MinOrder,
    decimal DeliveryFee, StoreStatus Status, Guid? WarehouseId = null, Guid? DeliveryItemId = null);

public sealed record StoreDto(Guid Id, string Slug, string NameEn, string NameAr, string? Phone, string? Host, string Currency, bool CodEnabled,
    decimal MinOrder, decimal DeliveryFee, StoreStatus Status, Guid? WarehouseId, Guid? DeliveryItemId);

public sealed record CategoryRequest(string Slug, string NameEn, string NameAr, Guid? ParentId, Guid? ItemGroupId, int SortOrder = 0, bool IsActive = true);

public sealed record CategoryDto(Guid Id, string Slug, string NameEn, string NameAr, Guid? ParentId, Guid? ItemGroupId, int SortOrder, bool IsActive);

/// <summary>Empty slug and titles default to the item's SKU and names.</summary>
public sealed record ListingRequest(Guid ItemId, string? Slug, string? TitleEn, string? TitleAr, string? DescriptionEn, string? DescriptionAr,
    Guid? CategoryId, bool IsPublished, int SortOrder = 0);

public sealed record PublishRequest(IReadOnlyList<Guid> ItemIds, bool Publish = true);

public sealed record ListingDto(Guid Id, Guid ItemId, string Sku, string Slug, string TitleEn, string TitleAr, string? DescriptionEn, string? DescriptionAr,
    Guid? CategoryId, bool IsPublished, int SortOrder);

/// <summary>Store settings, categories and listings of the signed-in company.</summary>
internal sealed class StoreAdminService(ErpDbContext db, ITenantContext tenant, IInventoryQueries inventory, IClock clock)
{
    public async Task<Store> StoreAsync(CancellationToken ct) =>
        await db.Set<Store>().SingleOrDefaultAsync(s => s.CompanyId == tenant.RequireCompanyId(), ct) ?? throw new NotFoundException("Store", "company");

    public async Task<string?> HostAsync(Store store, CancellationToken ct) =>
        await DirectoryOf(store).Select(d => d.Host).SingleOrDefaultAsync(ct);

    /// <summary>The directory has no row-level security: every query on it names the tenant and company explicitly.</summary>
    private IQueryable<StoreDomain> DirectoryOf(Store store) =>
        db.Set<StoreDomain>().Where(d => d.TenantId == tenant.RequireTenantId() && d.CompanyId == store.CompanyId && d.StoreId == store.Id);

    public Task<Store> SaveStoreAsync(StoreRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (nameEn, nameAr) = Bilingual.Require(r.NameEn, r.NameAr);
            var slug = (r.Slug ?? "").Trim().ToLowerInvariant();
            if (!Slugs.IsValid(slug, minLength: 3, maxLength: 60) || slug == Slugs.HostSlug)
                throw new BusinessRuleException("commerce.slug_invalid", "Use 3–60 lower-case letters, digits and hyphens for the store address.",
                    "استخدم من 3 إلى 60 حرفاً لاتينياً صغيراً أو رقماً أو شرطة لعنوان المتجر.");
            var host = string.IsNullOrWhiteSpace(r.Host) ? null : r.Host.Trim().ToLowerInvariant();
            if (host is not null && (host.Length > 253 || !Uri.CheckHostName(host).Equals(UriHostNameType.Dns) || !host.Contains('.')))
                throw new BusinessRuleException("commerce.host_invalid", "Enter the domain only, such as shop.example.qa.", "أدخل اسم النطاق فقط، مثل shop.example.qa.");
            if (r.MinOrder < 0 || r.DeliveryFee < 0)
                throw new BusinessRuleException("commerce.amount_negative", "Amounts cannot be negative.", "لا يمكن أن تكون المبالغ سالبة.");

            if (r.WarehouseId is { } warehouseId && await inventory.GetWarehouseAsync(warehouseId, token) is not { IsActive: true })
                throw new NotFoundException("Warehouse", warehouseId);
            if (r.DeliveryItemId is { } deliveryItemId
                && (await inventory.GetItemsAsync([deliveryItemId], token)).GetValueOrDefault(deliveryItemId) is not { IsActive: true, Type: ItemType.Service })
                throw new BusinessRuleException("commerce.delivery_item", "Choose an active service item for the delivery fee.", "اختر صنف خدمة نشطاً لرسوم التوصيل.");

            var companyId = tenant.RequireCompanyId();
            var store = await db.Set<Store>().SingleOrDefaultAsync(s => s.CompanyId == companyId, token);
            var entry = store is null ? null : await DirectoryOf(store).SingleOrDefaultAsync(token);
            if (await db.Set<StoreDomain>().AnyAsync(d => d.StoreSlug == slug && (store == null || d.StoreId != store.Id), token))
                throw new ConflictException("commerce.slug_taken", "Another store already uses this address.", "عنوان المتجر مستخدم لمتجر آخر.");
            if (host is not null && await db.Set<StoreDomain>().AnyAsync(d => d.Host == host && (store == null || d.StoreId != store.Id), token))
                throw new ConflictException("commerce.host_taken", "Another store already uses this domain.", "النطاق مستخدم لمتجر آخر.");

            if (store is null)
            {
                store = new Store { CompanyId = companyId, Slug = slug, NameEn = nameEn, NameAr = nameAr };
                db.Add(store);
            }
            store.Slug = slug;
            store.NameEn = nameEn;
            store.NameAr = nameAr;
            store.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
            store.CodEnabled = r.CodEnabled;
            store.MinOrder = Money.Round(r.MinOrder);
            store.DeliveryFee = Money.Round(r.DeliveryFee);
            store.Status = r.Status;
            store.WarehouseId = r.WarehouseId;
            store.DeliveryItemId = r.DeliveryItemId;

            // The slug is the directory's key: a new slug replaces the row.
            if (entry is not null && entry.StoreSlug != slug)
            {
                db.Remove(entry);
                await db.SaveChangesAsync(token);
                entry = null;
            }
            if (entry is null)
            {
                entry = new StoreDomain { StoreSlug = slug, TenantId = tenant.RequireTenantId(), CompanyId = companyId, StoreId = store.Id, CreatedAt = clock.UtcNow };
                db.Add(entry);
            }
            else entry.UpdatedAt = clock.UtcNow;
            entry.Host = host;
            entry.IsActive = store.Status == StoreStatus.Open;
            await db.SaveChangesAsync(token);
            return store;
        }, ct);

    public Task<Category> SaveCategoryAsync(Guid? id, CategoryRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var store = await StoreAsync(token);
            var (nameEn, nameAr) = Bilingual.Require(r.NameEn, r.NameAr);
            var slug = SlugOrThrow(r.Slug, Slugs.From(r.NameEn, 80), 80);
            var category = id is { } existing
                ? await db.Set<Category>().SingleOrDefaultAsync(c => c.Id == existing && c.StoreId == store.Id, token) ?? throw new NotFoundException("Category", existing)
                : (Category)db.Add(new Category { CompanyId = store.CompanyId, StoreId = store.Id, Slug = slug, NameEn = nameEn, NameAr = nameAr }).Entity;
            if (await db.Set<Category>().AnyAsync(c => c.StoreId == store.Id && c.Slug == slug && c.Id != category.Id, token))
                throw new ConflictException("commerce.category_slug_taken", "Another category already uses this address.", "عنوان الفئة مستخدم لفئة أخرى.");
            if (r.ParentId is { } parent && (parent == category.Id || !await db.Set<Category>().AnyAsync(c => c.Id == parent && c.StoreId == store.Id, token)))
                throw new NotFoundException("Category", parent);
            if (r.ItemGroupId is { } group && !await inventory.ItemGroupExistsAsync(group, token))
                throw new NotFoundException("ItemGroup", group);
            category.Slug = slug;
            category.NameEn = nameEn;
            category.NameAr = nameAr;
            category.ParentId = r.ParentId;
            category.ItemGroupId = r.ItemGroupId;
            category.SortOrder = r.SortOrder;
            category.IsActive = r.IsActive;
            await db.SaveChangesAsync(token);
            return category;
        }, ct);

    public Task<Listing> SaveListingAsync(Guid? id, ListingRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var store = await StoreAsync(token);
            var listing = id is { } existing
                ? await db.Set<Listing>().SingleOrDefaultAsync(l => l.Id == existing && l.StoreId == store.Id, token) ?? throw new NotFoundException("Listing", existing)
                : null;
            var itemId = listing?.ItemId ?? r.ItemId;
            var item = (await inventory.GetItemsAsync([itemId], token)).GetValueOrDefault(itemId) ?? throw new NotFoundException("Item", itemId);
            if (listing is null)
            {
                if (await db.Set<Listing>().AnyAsync(l => l.StoreId == store.Id && l.ItemId == itemId, token))
                    throw new ConflictException("commerce.item_listed", $"{item.Sku} is already in the store.", $"الصنف {item.Sku} موجود في المتجر.");
                listing = (Listing)db.Add(new Listing { CompanyId = store.CompanyId, StoreId = store.Id, ItemId = itemId, Slug = "", TitleEn = "", TitleAr = "" }).Entity;
            }
            var slug = SlugOrThrow(r.Slug, string.IsNullOrWhiteSpace(listing.Slug) ? Slugs.From(item.Sku) : listing.Slug, 120);
            if (await db.Set<Listing>().AnyAsync(l => l.StoreId == store.Id && l.Slug == slug && l.Id != listing.Id, token))
                throw new ConflictException("commerce.listing_slug_taken", "Another product already uses this address.", "عنوان المنتج مستخدم لمنتج آخر.");
            if (r.CategoryId is { } category && !await db.Set<Category>().AnyAsync(c => c.Id == category && c.StoreId == store.Id, token))
                throw new NotFoundException("Category", category);
            var (titleEn, titleAr) = Bilingual.Require(string.IsNullOrWhiteSpace(r.TitleEn) ? item.NameEn : r.TitleEn,
                string.IsNullOrWhiteSpace(r.TitleAr) ? item.NameAr : r.TitleAr, "title");
            listing.Slug = slug;
            listing.TitleEn = titleEn;
            listing.TitleAr = titleAr;
            listing.DescriptionEn = string.IsNullOrWhiteSpace(r.DescriptionEn) ? null : r.DescriptionEn.Trim();
            listing.DescriptionAr = string.IsNullOrWhiteSpace(r.DescriptionAr) ? null : r.DescriptionAr.Trim();
            listing.CategoryId = r.CategoryId;
            listing.IsPublished = r.IsPublished;
            listing.SortOrder = r.SortOrder;
            await db.SaveChangesAsync(token);
            return listing;
        }, ct);

    /// <summary>Publishes (or hides) several items at once, listing the ones not in the store yet under their SKU and names.</summary>
    public Task<int> PublishAsync(PublishRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var store = await StoreAsync(token);
            var ids = (r.ItemIds ?? []).Distinct().ToList();
            var items = await inventory.GetItemsAsync(ids, token);
            if (ids.FirstOrDefault(i => !items.ContainsKey(i)) is var missing && missing != Guid.Empty) throw new NotFoundException("Item", missing);
            var listings = await db.Set<Listing>().Where(l => l.StoreId == store.Id && ids.Contains(l.ItemId)).ToListAsync(token);
            var slugs = (await db.Set<Listing>().Where(l => l.StoreId == store.Id).Select(l => l.Slug).ToListAsync(token)).ToHashSet();
            foreach (var id in ids)
            {
                var listing = listings.SingleOrDefault(l => l.ItemId == id);
                if (listing is null)
                {
                    if (!r.Publish) continue;
                    var item = items[id];
                    var slug = Slugs.From(item.Sku) is { Length: > 0 } s ? s : "item-" + id.ToString("N")[..8];
                    for (var n = 2; slugs.Contains(slug); n++) slug = $"{Slugs.From(item.Sku)}-{n}";
                    slugs.Add(slug);
                    listing = new Listing { CompanyId = store.CompanyId, StoreId = store.Id, ItemId = id, Slug = slug, TitleEn = item.NameEn, TitleAr = item.NameAr };
                    db.Add(listing);
                }
                listing.IsPublished = r.Publish;
            }
            await db.SaveChangesAsync(token);
            return ids.Count;
        }, ct);

    private static string SlugOrThrow(string? requested, string fallback, int maxLength)
    {
        var slug = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim().ToLowerInvariant();
        if (!Slugs.IsValid(slug, maxLength: maxLength))
            throw new BusinessRuleException("commerce.slug_invalid", "Use lower-case letters, digits and hyphens for the address.",
                "استخدم حروفاً لاتينية صغيرة وأرقاماً وشرطات للعنوان.");
        return slug;
    }
}

internal static class StoreAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/commerce").WithTags("Commerce").RequireAuthorization();

        g.MapGet("/store", async (StoreAdminService s, CancellationToken ct) =>
        {
            var store = await s.StoreAsync(ct);
            return ToDto(store, await s.HostAsync(store, ct));
        }).RequirePermission(CommercePermissions.StoreManage);
        g.MapPut("/store", async (StoreRequest r, StoreAdminService s, CancellationToken ct) =>
        {
            var store = await s.SaveStoreAsync(r, ct);
            return ToDto(store, await s.HostAsync(store, ct));
        }).RequirePermission(CommercePermissions.StoreManage);

        g.MapGet("/categories", async (StoreAdminService s, ErpDbContext db, CancellationToken ct) =>
        {
            var store = await s.StoreAsync(ct);
            return (await db.Set<Category>().AsNoTracking().Where(c => c.StoreId == store.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync(ct))
                .Select(ToDto);
        }).RequirePermission(CommercePermissions.ListingEdit);
        g.MapPost("/categories", async (CategoryRequest r, StoreAdminService s, CancellationToken ct) => ToDto(await s.SaveCategoryAsync(null, r, ct)))
            .RequirePermission(CommercePermissions.ListingEdit);
        g.MapPut("/categories/{id:guid}", async (Guid id, CategoryRequest r, StoreAdminService s, CancellationToken ct) => ToDto(await s.SaveCategoryAsync(id, r, ct)))
            .RequirePermission(CommercePermissions.ListingEdit);

        g.MapGet("/listings", async (StoreAdminService s, ErpDbContext db, IInventoryQueries inventory, CancellationToken ct) =>
        {
            var store = await s.StoreAsync(ct);
            var listings = await db.Set<Listing>().AsNoTracking().Where(l => l.StoreId == store.Id).OrderBy(l => l.SortOrder).ThenBy(l => l.TitleEn).ToListAsync(ct);
            var items = await inventory.GetItemsAsync(listings.Select(l => l.ItemId), ct);
            return listings.Select(l => ToDto(l, items.GetValueOrDefault(l.ItemId)?.Sku ?? ""));
        }).RequirePermission(CommercePermissions.ListingEdit);
        g.MapPost("/listings", async (ListingRequest r, StoreAdminService s, IInventoryQueries inventory, CancellationToken ct) =>
        {
            var listing = await s.SaveListingAsync(null, r, ct);
            return ToDto(listing, (await inventory.GetItemsAsync([listing.ItemId], ct))[listing.ItemId].Sku);
        }).RequirePermission(CommercePermissions.ListingEdit);
        g.MapPut("/listings/{id:guid}", async (Guid id, ListingRequest r, StoreAdminService s, IInventoryQueries inventory, CancellationToken ct) =>
        {
            var listing = await s.SaveListingAsync(id, r, ct);
            return ToDto(listing, (await inventory.GetItemsAsync([listing.ItemId], ct))[listing.ItemId].Sku);
        }).RequirePermission(CommercePermissions.ListingEdit);
        g.MapPost("/listings/publish", async (PublishRequest r, StoreAdminService s, CancellationToken ct) => new { count = await s.PublishAsync(r, ct) })
            .RequirePermission(CommercePermissions.ListingEdit);
    }

    private static StoreDto ToDto(Store s, string? host) =>
        new(s.Id, s.Slug, s.NameEn, s.NameAr, s.Phone, host, s.Currency, s.CodEnabled, s.MinOrder, s.DeliveryFee, s.Status, s.WarehouseId, s.DeliveryItemId);

    private static CategoryDto ToDto(Category c) => new(c.Id, c.Slug, c.NameEn, c.NameAr, c.ParentId, c.ItemGroupId, c.SortOrder, c.IsActive);

    private static ListingDto ToDto(Listing l, string sku) =>
        new(l.Id, l.ItemId, sku, l.Slug, l.TitleEn, l.TitleAr, l.DescriptionEn, l.DescriptionAr, l.CategoryId, l.IsPublished, l.SortOrder);
}

using System.Text.RegularExpressions;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Commerce.Domain;

/// <summary>Only an open store is reachable by shoppers; a draft is being set up, a closed one is switched off.</summary>
public enum StoreStatus { Draft, Open, Closed }

/// <summary>One online store per company. Its slug is unique across all tenants (see <see cref="StoreDomain"/>).</summary>
public sealed class Store : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Slug { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public string? Phone { get; set; }
    public string Currency { get; set; } = "QAR";
    public bool CodEnabled { get; set; } = true;
    public decimal MinOrder { get; set; }
    public decimal DeliveryFee { get; set; }
    public StoreStatus Status { get; set; } = StoreStatus.Draft;

    /// <summary>Where online orders are sent from; checkout is closed until it is set.</summary>
    public Guid? WarehouseId { get; set; }

    /// <summary>The service item the delivery fee is charged as; needed when there is a delivery fee.</summary>
    public Guid? DeliveryItemId { get; set; }
    public uint Version { get; set; }
}

/// <summary>
/// The public store directory: slug (and optional custom domain) → tenant and company. Not tenant-scoped (read before a tenant is
/// known), so it carries only keys; written only together with its <see cref="Store"/>, with the tenant filtered explicitly.
/// </summary>
public sealed class StoreDomain
{
    public required string StoreSlug { get; set; }
    public string? Host { get; set; }
    public Guid TenantId { get; init; }
    public Guid CompanyId { get; init; }
    public Guid StoreId { get; init; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Web navigation. <see cref="ItemGroupId"/> also places every listed item of that inventory group in the category.</summary>
public sealed class Category : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid StoreId { get; init; }
    public required string Slug { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? ItemGroupId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>An inventory item shown in the store: its own slug and text. Price and stock always come from Sales and Inventory.</summary>
public sealed class Listing : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid StoreId { get; init; }
    public Guid ItemId { get; init; }
    public Guid? CategoryId { get; set; }
    public required string Slug { get; set; }
    public required string TitleEn { get; set; }
    public required string TitleAr { get; set; }
    public string? DescriptionEn { get; set; }
    public string? DescriptionAr { get; set; }
    public bool IsPublished { get; set; }
    public int SortOrder { get; set; }
    public uint Version { get; set; }
}

public sealed class ListingImage : TenantEntity
{
    public Guid ListingId { get; init; }
    public required string StorageKey { get; set; }
    public int SortOrder { get; set; }
}

public enum CartStatus { Open, CheckedOut }

/// <summary>An anonymous shopper's basket, found by the SHA-256 hash of the key the browser keeps.</summary>
public sealed class Cart : TenantEntity
{
    public Guid StoreId { get; init; }
    public required string TokenHash { get; init; }
    public CartStatus Status { get; set; } = CartStatus.Open;
    public DateTimeOffset ExpiresAt { get; set; }
    public List<CartLine> Lines { get; init; } = [];
}

/// <summary>A product in a cart, in <see cref="UomId"/> (a pack such as a box) or, without it, the item's base unit.</summary>
public sealed class CartLine : TenantEntity
{
    public Guid CartId { get; init; }
    public Guid ListingId { get; init; }
    public Guid ItemId { get; init; }
    public Guid? UomId { get; init; }
    public decimal Quantity { get; set; }
}

public enum CheckoutStatus { Placed, Cancelled }

public enum PaymentMethod { Cod }

/// <summary>One purchase of a cart: the shopper, the Qatar delivery address, what was charged and the sales order it became.</summary>
public sealed class Checkout : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid StoreId { get; init; }
    public Guid CartId { get; init; }
    public required string OrderNo { get; init; }
    public CheckoutStatus Status { get; set; } = CheckoutStatus.Placed;
    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cod;
    public required string CustomerName { get; init; }
    public required string Phone { get; init; }
    public required string Zone { get; init; }
    public required string Street { get; init; }
    public required string Building { get; init; }
    public string? Unit { get; init; }
    public string? AddressNotes { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public decimal Subtotal { get; init; }
    public decimal DeliveryFee { get; init; }
    public decimal Total { get; init; }

    /// <summary>The priced lines as the shopper confirmed them (JSON).</summary>
    public required string PricedSnapshot { get; init; }
    public Guid SalesOrderId { get; init; }
    public required string StatusTokenHash { get; init; }

    public string Address => $"Zone {Zone}, Street {Street}, Building {Building}" + (Unit is null ? "" : $", Unit {Unit}");
}

public enum ReservationStatus { Held, Converted, Released }

/// <summary>A soft stock hold made at checkout (design §5.2).</summary>
public sealed class Reservation : TenantEntity
{
    public Guid StoreId { get; init; }
    public Guid CartId { get; init; }
    public Guid? CheckoutId { get; set; }
    public Guid ItemId { get; init; }
    public Guid WarehouseId { get; init; }
    public decimal Quantity { get; init; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Held;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>URL slugs: lower-case letters, digits and single hyphens.</summary>
public static partial class Slugs
{
    /// <summary>Reserved: <c>/api/store/_/…</c> finds the store by the request's host (a custom domain) instead of a slug.</summary>
    public const string HostSlug = "_";

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex Valid();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Invalid();

    public static bool IsValid(string? slug, int minLength = 1, int maxLength = 120) =>
        slug is not null && slug.Length >= minLength && slug.Length <= maxLength && Valid().IsMatch(slug) && !slug.Contains("--");

    /// <summary>A slug made from free text (an SKU, an English name); empty when nothing usable is left.</summary>
    public static string From(string? text, int maxLength = 120)
    {
        var slug = Invalid().Replace((text ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > maxLength ? slug[..maxLength].TrimEnd('-') : slug;
    }
}

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> b)
    {
        b.ToTable("store", "commerce");
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class StoreDomainConfiguration : IEntityTypeConfiguration<StoreDomain>
{
    public void Configure(EntityTypeBuilder<StoreDomain> b)
    {
        b.ToTable("store_domain", "commerce");
        b.HasKey(x => x.StoreSlug);
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b) => b.ToTable("category", "commerce");
}

internal sealed class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> b)
    {
        b.ToTable("listing", "commerce");
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> b)
    {
        b.ToTable("cart", "commerce");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CartId);
    }
}

internal sealed class CartLineConfiguration : IEntityTypeConfiguration<CartLine>
{
    public void Configure(EntityTypeBuilder<CartLine> b)
    {
        b.ToTable("cart_line", "commerce");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

internal sealed class CheckoutConfiguration : IEntityTypeConfiguration<Checkout>
{
    public void Configure(EntityTypeBuilder<Checkout> b)
    {
        b.ToTable("checkout", "commerce");
        b.Property(x => x.PricedSnapshot).HasColumnType("jsonb");
        b.Property(x => x.Latitude).HasPrecision(9, 6);
        b.Property(x => x.Longitude).HasPrecision(9, 6);
        b.Ignore(x => x.Address);
    }
}

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> b)
    {
        b.ToTable("reservation", "commerce");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

internal sealed class ListingImageConfiguration : IEntityTypeConfiguration<ListingImage>
{
    public void Configure(EntityTypeBuilder<ListingImage> b) => b.ToTable("listing_image", "commerce");
}

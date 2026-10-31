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

internal sealed class ListingImageConfiguration : IEntityTypeConfiguration<ListingImage>
{
    public void Configure(EntityTypeBuilder<ListingImage> b) => b.ToTable("listing_image", "commerce");
}

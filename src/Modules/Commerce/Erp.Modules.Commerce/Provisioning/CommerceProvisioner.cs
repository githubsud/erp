using Erp.Infrastructure.Persistence;
using Erp.Modules.Commerce.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Commerce.Provisioning;

/// <summary>
/// Every company starts with a draft store named after it, with the tenant code as slug (a number is added when another store already
/// uses it). Shoppers cannot reach it until the owner opens it.
/// </summary>
internal sealed class CommerceProvisioner(ErpDbContext db, IClock clock) : ITenantProvisioner
{
    public int Order => 70;

    public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        var slug = await FreeSlugAsync(db, Slugs.From(context.TenantCode, 50), ct);
        var store = new Store { CompanyId = context.CompanyId, Slug = slug, NameEn = context.CompanyNameEn, NameAr = context.CompanyNameAr };
        db.Add(store);
        db.Add(new StoreDomain
        {
            StoreSlug = slug, TenantId = context.TenantId, CompanyId = context.CompanyId, StoreId = store.Id, CreatedAt = clock.UtcNow
        });
    }

    /// <summary><paramref name="wanted"/>, or wanted-2, wanted-3… when the directory already holds it (slugs are unique across tenants).</summary>
    internal static async Task<string> FreeSlugAsync(ErpDbContext db, string wanted, CancellationToken ct)
    {
        if (!Slugs.IsValid(wanted, minLength: 3, maxLength: 50)) wanted = "store-" + Guid.NewGuid().ToString("N")[..8];
        var taken = await db.Set<StoreDomain>().AsNoTracking()
            .Where(d => d.StoreSlug == wanted || d.StoreSlug.StartsWith(wanted + "-")).Select(d => d.StoreSlug).ToListAsync(ct);
        if (!taken.Contains(wanted)) return wanted;
        var n = 2;
        while (taken.Contains($"{wanted}-{n}")) n++;
        return $"{wanted}-{n}";
    }
}

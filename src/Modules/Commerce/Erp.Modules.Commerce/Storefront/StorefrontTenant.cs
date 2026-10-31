using Erp.Infrastructure.Persistence;
using Erp.Modules.Commerce.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Commerce.Storefront;

/// <summary>
/// Shoppers have no staff token, so the storefront finds its tenant from the store instead (design §3): <c>/api/store/{slug}/…</c>
/// by slug, or <c>/api/store/_/…</c> by the request's host (a custom domain). The directory row gives tenant and company, which go
/// into <see cref="ITenantContext"/>; from then on EF filters and PostgreSQL row-level security apply as for any request.
/// Unknown, draft and closed stores, suspended customers and companies without the online-store module all answer 404 alike.
/// </summary>
internal static class StorefrontTenant
{
    private const string ItemKey = "commerce.store";

    public static async ValueTask<object?> ResolveAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var slug = (http.GetRouteValue("slug") as string ?? "").Trim().ToLowerInvariant();
        var host = http.Request.Host.Host.Trim().ToLowerInvariant();
        var db = services.GetRequiredService<ErpDbContext>();
        // The directory is the only commerce table without row-level security; it is read before any tenant is set.
        var entry = await db.Set<StoreDomain>().AsNoTracking()
                        .Where(d => d.IsActive && (slug == Slugs.HostSlug ? d.Host == host : d.StoreSlug == slug))
                        .SingleOrDefaultAsync(http.RequestAborted)
                    ?? throw NotFound(slug);

        var tenant = services.GetRequiredService<ITenantContext>();
        tenant.Set(entry.TenantId, entry.CompanyId, SystemUser.Id);
        var modules = services.GetRequiredService<IModuleAccess>();
        if (await modules.SuspendedAsync(http.RequestAborted) || (await modules.DisabledAsync(http.RequestAborted)).Contains(ModuleCodes.Commerce))
            throw NotFound(slug);
        // Read through row-level security as well: the store must belong to the tenant just set and be open.
        var store = await db.Set<Store>().AsNoTracking()
                        .SingleOrDefaultAsync(s => s.Id == entry.StoreId && s.CompanyId == entry.CompanyId && s.Status == StoreStatus.Open, http.RequestAborted)
                    ?? throw NotFound(slug);
        http.Items[ItemKey] = store;
        return await next(context);
    }

    /// <summary>The store resolved for this request (only inside the storefront group).</summary>
    public static Store Current(HttpContext http) =>
        http.Items[ItemKey] as Store ?? throw new InvalidOperationException("No storefront store was resolved for this request.");

    private static NotFoundException NotFound(string slug) => new("Store", slug);
}

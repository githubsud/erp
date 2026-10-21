using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Core.Platform;

public sealed record GettingStartedDto(bool Dismissed, IReadOnlyList<GettingStartedStep> Steps);

/// <summary>The company's details on receipts and invoices, and a login for each person working in the shop.</summary>
internal sealed class CoreGettingStarted(ErpDbContext db, ITenantContext tenant) : IGettingStartedProvider
{
    public string Permission => CorePermissions.CompanyManage;

    public async Task<IReadOnlyList<GettingStartedStep>> GetStepsAsync(CancellationToken ct)
    {
        var company = await db.Set<Company>().AsNoTracking().SingleAsync(c => c.Id == tenant.RequireCompanyId(), ct);
        var users = await db.Set<AppUser>().CountAsync(u => u.IsActive, ct);
        return
        [
            new("company", 10, "Add your shop's details", "أضف بيانات محلك",
                "Phone, address and CR number appear on every receipt and invoice.", "الهاتف والعنوان ورقم السجل التجاري تظهر على كل إيصال وفاتورة.",
                "/settings/company", !string.IsNullOrWhiteSpace(company.Phone) && !string.IsNullOrWhiteSpace(company.AddressAr ?? company.AddressEn)),
            new("users", 20, "Give your staff their own login", "أنشئ دخولاً لكل موظف",
                "Each cashier signs in with their own name, so you see who sold what.", "كل كاشير يدخل باسمه، لتعرف من باع ماذا.",
                "/settings/users", users > 1)
        ];
    }
}

internal static class GettingStartedEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/core/getting-started").WithTags("Core").RequireAuthorization();

        // Every module's steps that this user can act on; a module that fails is left out instead of breaking the page.
        g.MapGet("", async (IEnumerable<IGettingStartedProvider> providers, IPermissionService permissions, IModuleAccess modules, ErpDbContext db,
            ITenantContext tenant, ILoggerFactory logs, CancellationToken ct) =>
        {
            var steps = new List<GettingStartedStep>();
            var off = await modules.DisabledAsync(ct);
            foreach (var p in providers)
            {
                if (!await permissions.HasAsync(p.Permission, ct) || (p.Module is { } m && off.Contains(m))) continue;
                try
                {
                    steps.AddRange(await p.GetStepsAsync(ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logs.CreateLogger("GettingStarted").LogWarning(ex, "Getting-started provider {Provider} failed", p.GetType().Name);
                }
            }
            var dismissed = await db.Set<Company>().Where(c => c.Id == tenant.RequireCompanyId()).Select(c => c.GettingStartedHiddenAt != null).SingleAsync(ct);
            return new GettingStartedDto(dismissed, steps.OrderBy(s => s.Order).ToList());
        });

        g.MapPost("/hide", (ErpDbContext db, ITenantContext tenant, IClock clock, CancellationToken ct) => SetHiddenAsync(db, tenant, clock.UtcNow, ct))
            .RequirePermission(CorePermissions.CompanyManage);
        g.MapPost("/show", (ErpDbContext db, ITenantContext tenant, CancellationToken ct) => SetHiddenAsync(db, tenant, null, ct))
            .RequirePermission(CorePermissions.CompanyManage);
    }

    private static async Task<IResult> SetHiddenAsync(ErpDbContext db, ITenantContext tenant, DateTimeOffset? at, CancellationToken ct)
    {
        var company = await db.Set<Company>().SingleAsync(c => c.Id == tenant.RequireCompanyId(), ct);
        company.GettingStartedHiddenAt = at;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Http;

namespace Erp.Infrastructure.Web;

/// <summary>Refuses API calls to a module the company has switched off (its data stays untouched).</summary>
public sealed class ModuleGateMiddleware(RequestDelegate next)
{
    private static readonly (string Prefix, string Module)[] Routes =
    [
        ("/api/hr", ModuleCodes.Hr), ("/api/me", ModuleCodes.Hr), ("/api/inventory", ModuleCodes.Inventory),
        ("/api/purchasing", ModuleCodes.Purchasing), ("/api/sales", ModuleCodes.Sales)
    ];

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, IModuleAccess modules)
    {
        var path = context.Request.Path.Value ?? "";
        var module = Routes.FirstOrDefault(r => path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)
                                                && (path.Length == r.Prefix.Length || path[r.Prefix.Length] == '/')).Module;
        if (module is not null && tenant.CompanyId is not null && (await modules.DisabledAsync(context.RequestAborted)).Contains(module))
            throw new ForbiddenException($"The {module} module is switched off for this company.");
        await next(context);
    }
}

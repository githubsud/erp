using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Http;

namespace Erp.Infrastructure.Web;

/// <summary>
/// Refuses API calls to a module that is not licensed or is switched off for the company (its data stays untouched), and turns a
/// suspended customer read-only.
/// </summary>
public sealed class ModuleGateMiddleware(RequestDelegate next)
{
    // Longest prefix first.
    private static readonly (string Prefix, string Module)[] Routes =
    [
        ("/api/purchasing/subcontracts", ModuleCodes.Subcontracts), ("/api/sales/projects", ModuleCodes.Projects),
        ("/api/sales/commissions", ModuleCodes.Crm), ("/api/finance/budgets", ModuleCodes.Budgets), ("/api/finance/assets", ModuleCodes.Assets),
        ("/api/sales/pos", ModuleCodes.Pos), ("/api/sales/crm", ModuleCodes.Crm), ("/api/purchasing", ModuleCodes.Purchasing),
        ("/api/inventory", ModuleCodes.Inventory), ("/api/tailoring", ModuleCodes.Tailoring), ("/api/salon", ModuleCodes.Salon), ("/api/ai", ModuleCodes.Ai), ("/api/workshop", ModuleCodes.Workshop),
        ("/api/sales", ModuleCodes.Sales), ("/api/hr", ModuleCodes.Hr), ("/api/me", ModuleCodes.Ess)
    ];

    public static string? ModuleOf(string path) =>
        Routes.FirstOrDefault(r => path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)
                                   && (path.Length == r.Prefix.Length || path[r.Prefix.Length] == '/')).Module;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, IModuleAccess modules)
    {
        if (tenant.TenantId is null || tenant.CompanyId is null)
        {
            await next(context);
            return;
        }
        var path = context.Request.Path.Value ?? "";
        var module = ModuleOf(path);
        if (module is not null && (await modules.DisabledAsync(context.RequestAborted)).Contains(module))
            throw new ForbiddenException($"The {module} module is not active for this company.");
        var isWrite = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                      && !HttpMethods.IsOptions(context.Request.Method);
        if (isWrite && !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) && await modules.SuspendedAsync(context.RequestAborted))
            throw new BusinessRuleException("tenant.suspended",
                "Your subscription is suspended: data can be viewed and exported but not changed. Please contact us to renew.",
                "اشتراككم موقوف: يمكن عرض البيانات وتصديرها دون تعديلها. يرجى التواصل معنا للتجديد.");
        await next(context);
    }
}

using Erp.Infrastructure.Web;
using Erp.Modules.Commerce.Storefront;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Commerce;

/// <summary>
/// The online store: store settings, published listings and the public catalog (docs/Commerce-Storefront-Design.md). Products, stock,
/// prices and orders stay in Inventory and Sales; Commerce reaches them only through their Contracts.
/// </summary>
public sealed class CommerceModule : IModule
{
    public string Name => "Commerce";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Admin.StoreAdminService>();
        services.AddScoped<Storefront.StorefrontCatalog>();
        services.AddScoped<Storefront.StockAvailability>();
        services.AddScoped<Storefront.CartService>();
        services.AddScoped<Storefront.CheckoutService>();
        services.AddStorefrontLimits(configuration);
        services.AddScoped<ITenantProvisioner, Provisioning.CommerceProvisioner>();
        services.AddSingleton<IPermissionProvider, CommercePermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        Admin.StoreAdminEndpoints.Map(app);
        Storefront.StorefrontEndpoints.Map(app);
    }
}

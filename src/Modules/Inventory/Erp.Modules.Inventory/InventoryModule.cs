using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Documents;
using Erp.Modules.Inventory.Endpoints;
using Erp.Modules.Inventory.Provisioning;
using Erp.Modules.Inventory.Stock;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Inventory;

public sealed class InventoryModule : IModule
{
    public string Name => "Inventory";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IStockService, StockLedger>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddScoped<InventoryMasterService>();
        services.AddScoped<StockAdjustmentService>();
        services.AddScoped<StockTransferService>();
        services.AddScoped<StockCountService>();
        services.AddScoped<ITenantProvisioner, InventoryProvisioner>();
        services.AddSingleton<IPermissionProvider, InventoryPermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => InventoryEndpoints.Map(app);
}

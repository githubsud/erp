using Erp.Infrastructure.Web;
using Erp.Modules.Purchasing.Documents;
using Erp.Modules.Purchasing.Endpoints;
using Erp.Modules.Purchasing.Provisioning;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Purchasing;

public sealed class PurchasingModule : IModule
{
    public string Name => "Purchasing";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SupplierService>();
        services.AddScoped<PurchaseOrderService>();
        services.AddScoped<IApprovalDocument, PurchaseOrderApproval>();
        services.AddScoped<PurchaseRequestService>();
        services.AddScoped<IApprovalDocument, PurchaseRequestApproval>();
        services.AddScoped<GoodsReceiptService>();
        services.AddScoped<SupplierInvoiceService>();
        services.AddScoped<LandedCostService>();
        services.AddScoped<SupplierPaymentService>();
        services.AddScoped<SupplierOpeningService>();
        services.AddScoped<DebitNoteService>();
        services.AddScoped<ITenantProvisioner, PurchasingProvisioner>();
        services.AddSingleton<IPermissionProvider, PurchasingPermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        PurchasingEndpoints.Map(app);
        PurchaseRequestEndpoints.Map(app);
    }
}

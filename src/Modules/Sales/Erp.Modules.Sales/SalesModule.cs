using Erp.Infrastructure.Web;
using Erp.Modules.Sales.Documents;
using Erp.Modules.Sales.Endpoints;
using Erp.Modules.Sales.Provisioning;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Sales;

public sealed class SalesModule : IModule
{
    public string Name => "Sales";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CustomerService>();
        services.AddScoped<CreditControlService>();
        services.AddScoped<QuotationService>();
        services.AddScoped<SalesOrderService>();
        services.AddScoped<DeliveryNoteService>();
        services.AddScoped<SalesInvoiceService>();
        services.AddScoped<CreditNoteService>();
        services.AddScoped<CustomerReceiptService>();
        services.AddScoped<CustomerCreditService>();
        services.AddScoped<CustomerOpeningService>();
        services.AddScoped<SalesPdfDocuments>();
        services.AddScoped<ITenantProvisioner, SalesProvisioner>();
        services.AddSingleton<IPermissionProvider, SalesPermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        SalesEndpoints.Map(app);
        CustomerCreditEndpoints.Map(app);
    }
}

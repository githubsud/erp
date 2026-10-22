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
        services.AddScoped<CustomerImportService>();
        services.AddScoped<CreditControlService>();
        services.AddScoped<QuotationService>();
        services.AddScoped<SalesOrderService>();
        services.AddScoped<DeliveryNoteService>();
        services.AddScoped<SalesInvoiceService>();
        services.AddScoped<CreditNoteService>();
        services.AddScoped<CustomerReceiptService>();
        services.AddScoped<CustomerCreditService>();
        services.AddScoped<Crm.CrmService>();
        services.AddScoped<Crm.CommissionService>();
        services.AddScoped<Projects.ProjectService>();
        services.AddScoped<Pos.PosService>();
        services.AddScoped<Pos.PosLimitService>();
        services.AddScoped<Tailoring.TailoringService>();
        services.AddScoped<Salon.SalonManager>();
        services.AddScoped<Workshop.WorkshopService>();
        services.AddScoped<IScheduledReport, Reports.CustomerAgingScheduledReport>();
        services.AddScoped<IDashboardProvider, Reports.SalesDashboard>();
        services.AddScoped<IGettingStartedProvider, Pos.PosGettingStarted>();
        services.AddScoped<IDashboardProvider, Reports.TodayDashboard>();
        services.AddScoped<IAiTool, Reports.CustomerBalancesAiTool>();
        services.AddScoped<IAiTool, Reports.SalesSummaryAiTool>();
        services.AddScoped<IAiTool, Reports.TailoringAiTool>();
        services.AddScoped<IAiTool, Reports.WorkshopAiTool>();
        services.AddScoped<Reports.LeakageService>();
        services.AddScoped<IDashboardProvider, Reports.LeakageDashboard>();
        services.AddScoped<IAiTool, Reports.LeakageAiTool>();
        services.AddScoped<CustomerOpeningService>();
        services.AddScoped<SalesPdfDocuments>();
        services.AddScoped<ITenantProvisioner, SalesProvisioner>();
        services.AddSingleton<IPermissionProvider, SalesPermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        SalesEndpoints.Map(app);
        CustomerCreditEndpoints.Map(app);
        Crm.CrmEndpoints.Map(app);
        Projects.ProjectEndpoints.Map(app);
        Pos.PosEndpoints.Map(app);
        Pos.PosLimitEndpoints.Map(app);
        Tailoring.TailoringEndpoints.Map(app);
        Salon.SalonEndpoints.Map(app);
        Workshop.WorkshopEndpoints.Map(app);
        Reports.LeakageEndpoints.Map(app);
    }
}

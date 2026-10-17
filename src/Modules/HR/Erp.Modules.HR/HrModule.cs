using Erp.Infrastructure.Web;
using Erp.Modules.HR.Documents;
using Erp.Modules.HR.Endpoints;
using Erp.Modules.HR.Payroll;
using Erp.Modules.HR.People;
using Erp.Modules.HR.Provisioning;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.HR;

public sealed class HrModule : IModule
{
    public string Name => "HR";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<HrSettingsProvider>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<ContractService>();
        services.AddScoped<LeaveService>();
        services.AddScoped<PayrollInputService>();
        services.AddScoped<PayrollService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<ITenantProvisioner, HrProvisioner>();
        services.AddSingleton<IPermissionProvider, HrPermissionProvider>();

        services.AddSingleton<DocumentExpiryJob>();
        if (configuration.GetValue("Jobs:DocumentExpiry:Enabled", true))
            services.AddHostedService(sp => sp.GetRequiredService<DocumentExpiryJob>());
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => HrEndpoints.Map(app);
}

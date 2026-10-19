using Erp.Modules.Core.Approvals;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Auth;
using Erp.Modules.Core.Domain;
using Erp.Modules.Core.Endpoints;
using Erp.Modules.Core.Provisioning;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Core;

public sealed class CoreModule : IModule
{
    public string Name => "Core";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        services.AddSingleton(jwt);
        services.AddMemoryCache();
        services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<AuthService>();
        services.AddScoped<TenantProvisioningService>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<ICompanyProfileProvider, CompanyProfileProvider>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<IModuleAccess, Platform.ModuleAccess>();
        services.AddScoped<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>());
        services.AddScoped<ITenantProvisioner, ApprovalProvisioner>();
        services.AddScoped<Platform.ReportScheduleService>();
        services.AddScoped<Platform.PlatformAuthService>();
        services.AddScoped<Platform.BackOfficeService>();
        services.AddSingleton<IPasswordHasher<Platform.PlatformAdmin>, PasswordHasher<Platform.PlatformAdmin>>();
        services.AddSingleton<Platform.BillingJob>();
        if (configuration.GetValue("Jobs:Billing:Enabled", true))
            services.AddHostedService(sp => sp.GetRequiredService<Platform.BillingJob>());
        services.AddScoped<Erp.Infrastructure.Outbox.IOutboxHandler, Platform.ScheduledReportEmailHandler>();
        services.AddSingleton<Platform.ReportScheduleJob>();
        if (configuration.GetValue("Jobs:ReportSchedules:Enabled", true))
            services.AddHostedService(sp => sp.GetRequiredService<Platform.ReportScheduleJob>());
        services.AddSingleton<IPermissionProvider, CorePermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        CoreEndpoints.Map(app);
        ApprovalEndpoints.Map(app);
        Platform.PlatformEndpoints.Map(app);
        Platform.ReportScheduleEndpoints.Map(app);
        Platform.DashboardEndpoints.Map(app);
        Platform.BackOfficeEndpoints.Map(app);
    }
}

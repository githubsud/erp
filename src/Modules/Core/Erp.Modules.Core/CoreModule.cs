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
        services.AddSingleton<IPermissionProvider, CorePermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => CoreEndpoints.Map(app);
}

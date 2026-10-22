using System.Reflection;
using Erp.Infrastructure.Email;
using Erp.Infrastructure.Numbering;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.Infrastructure.Tenancy;
using Erp.Infrastructure.Time;
using Erp.Infrastructure.Web;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddErpInfrastructure(this IServiceCollection services, IConfiguration configuration,
        IReadOnlyList<IModule> modules)
    {
        var database = configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var outbox = configuration.GetSection(OutboxOptions.Section).Get<OutboxOptions>() ?? new OutboxOptions();
        var security = configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
        var email = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
        FieldProtector.Configure(security);

        var moduleAssemblies = modules.Select(m => m.Assembly).ToList();
        var migrationAssemblies = new List<Assembly> { typeof(DependencyInjection).Assembly };
        migrationAssemblies.AddRange(moduleAssemblies);

        services.AddSingleton(database);
        services.AddSingleton(outbox);
        services.AddSingleton(new ModelAssemblyRegistry(moduleAssemblies));
        services.AddSingleton(sp => new DatabaseMigrator(database, migrationAssemblies, sp.GetRequiredService<ILogger<DatabaseMigrator>>()));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(email);
        if (string.IsNullOrWhiteSpace(email.Host))
        {
            services.AddSingleton<CapturingEmailSender>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<CapturingEmailSender>());
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantSessionInterceptor>();
        services.AddScoped<AuditingInterceptor>();

        services.AddDbContext<ErpDbContext>((sp, options) =>
        {
            options.UseNpgsql(database.AppConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_unused"))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>(), sp.GetRequiredService<AuditingInterceptor>())
                .ConfigureWarnings(ErpDbContext.ConfigureWarnings);
        });

        services.AddScoped<INumberingService, NumberingService>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddSignInRateLimit(configuration);
        services.AddExceptionHandler<ErpExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }
}

using System.Reflection;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Erp.Infrastructure.Persistence;

/// <summary>Assemblies whose <see cref="IEntityTypeConfiguration{TEntity}"/> classes form the model.</summary>
public sealed class ModelAssemblyRegistry(IEnumerable<Assembly> assemblies)
{
    public IReadOnlyList<Assembly> Assemblies { get; } = assemblies.Distinct().ToList();
}

/// <summary>
/// One DbContext for the whole modular monolith so that a document, its journal entry and its outbox
/// messages always commit in one transaction. Each module contributes its own entity configurations
/// (and owns its own schema); modules never touch each other's entities directly.
/// </summary>
public sealed class ErpDbContext(
    DbContextOptions<ErpDbContext> options,
    ITenantContext tenant,
    ModelAssemblyRegistry registry) : DbContext(options)
{
    private static readonly MethodInfo TenantFilterMethod =
        typeof(ErpDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Referenced by query filters; EF parameterises it per context instance.</summary>
    public Guid? CurrentTenantId => tenant.TenantId;

    public ITenantContext Tenant => tenant;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money is numeric(19,4) everywhere unless a configuration says otherwise. Never float.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ErpDbContext).Assembly);
        foreach (var assembly in registry.Assemblies)
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType) && entityType.BaseType is null)
                TenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);

            foreach (var property in entityType.GetProperties())
            {
                var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (enumType.IsEnum && property.GetValueConverter() is null)
                    property.SetValueConverter(SnakeCaseEnumConverter.Create(enumType));
            }
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

    /// <summary>Runs <paramref name="work"/> in one database transaction and commits only if everything succeeds.</summary>
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            var nested = await work(ct);
            await SaveChangesAsync(ct);
            return nested;
        }

        await using var transaction = await Database.BeginTransactionAsync(ct);
        var result = await work(ct);
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public Task InTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default) =>
        InTransactionAsync<bool>(async token => { await work(token); return true; }, ct);

    public void RequireTransaction(string operation)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException($"{operation} must run inside a database transaction.");
    }

    internal static void ConfigureWarnings(WarningsConfigurationBuilder warnings) =>
        // Principal and dependent always share the same tenant filter, so the interaction is intended.
        warnings.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning);
}

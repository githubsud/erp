using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Erp.Infrastructure.Persistence;

namespace Erp.Modules.Core.Provisioning;

/// <summary>Lists companies tenant by tenant; each tenant is read inside its own scope so row-level security applies.</summary>
internal sealed class TenantDirectory(ErpDbContext db, IServiceScopeFactory scopes) : ITenantDirectory
{
    public async Task<IReadOnlyList<TenantCompany>> GetActiveCompaniesAsync(CancellationToken ct = default)
    {
        var tenantIds = await db.Set<Tenant>().AsNoTracking().Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(ct);
        var result = new List<TenantCompany>();
        foreach (var tenantId in tenantIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, null);
            var scoped = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
            var companies = await scoped.Set<Company>().AsNoTracking().Select(c => c.Id).ToListAsync(ct);
            result.AddRange(companies.Select(c => new TenantCompany(tenantId, c)));
        }
        return result;
    }
}

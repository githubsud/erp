using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Sales.Provisioning;

internal sealed class SalesProvisioner(ErpDbContext db) : ITenantProvisioner
{
    public int Order => 50;

    public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        db.Add(new SalesSettings { CompanyId = context.CompanyId });
        return Task.CompletedTask;
    }
}

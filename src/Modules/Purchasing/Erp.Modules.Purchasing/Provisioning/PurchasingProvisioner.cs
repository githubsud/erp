using Erp.Infrastructure.Persistence;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Purchasing.Provisioning;

internal sealed class PurchasingProvisioner(ErpDbContext db) : ITenantProvisioner
{
    public int Order => 40;

    public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        db.Add(new PurchaseSettings { CompanyId = context.CompanyId });
        return Task.CompletedTask;
    }
}

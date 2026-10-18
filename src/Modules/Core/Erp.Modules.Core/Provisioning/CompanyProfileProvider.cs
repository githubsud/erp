using Erp.Infrastructure.Persistence;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Core.Provisioning;

internal sealed class CompanyProfileProvider(ErpDbContext db, ITenantContext tenant) : ICompanyProfileProvider
{
    public async Task<CompanyProfile> GetAsync(CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        return await db.Set<Company>().AsNoTracking().Where(c => c.Id == companyId)
                   .Select(c => new CompanyProfile(c.NameEn, c.NameAr, c.CrNo, c.EstablishmentId, c.AddressEn, c.AddressAr, c.Phone, c.Email, c.Tin))
                   .SingleOrDefaultAsync(ct)
               ?? throw new NotFoundException("Company", companyId);
    }
}

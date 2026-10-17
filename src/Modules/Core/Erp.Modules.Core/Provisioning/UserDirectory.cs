using Erp.Infrastructure.Persistence;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Core.Provisioning;

internal sealed class UserDirectory(ErpDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyList<string>> EmailsWithPermissionAsync(string permission, CancellationToken ct = default) =>
        await (from u in db.Set<AppUser>()
               join ur in db.Set<UserRole>() on u.Id equals ur.UserId
               join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
               where u.IsActive && (rp.Permission == permission || rp.Permission == DefaultRoles.Wildcard)
               select u.Email).Distinct().ToListAsync(ct);
}

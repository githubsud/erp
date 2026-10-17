using System.Text.RegularExpressions;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Core.Provisioning;

public sealed record ProvisionTenantRequest(
    string Code,
    string? NameEn,
    string? NameAr,
    string? CrNo,
    string? EstablishmentId,
    string AdminEmail,
    string AdminPassword,
    string? AdminNameEn,
    string? AdminNameAr,
    int FiscalYearStartMonth = 1,
    string PreferredLanguage = "ar");

public sealed record ProvisionTenantResponse(Guid TenantId, string Code, Guid CompanyId, Guid BranchId, Guid AdminUserId);

/// <summary>
/// Creates a ready-to-use tenant in one transaction: company, head-office branch, default roles, the owner
/// user, then each module's defaults (chart of accounts, fiscal year, payroll components, ...).
/// </summary>
public sealed partial class TenantProvisioningService(
    ErpDbContext db,
    ITenantContext tenantContext,
    IEnumerable<IPermissionProvider> permissionProviders,
    IEnumerable<ITenantProvisioner> provisioners,
    IPasswordHasher<AppUser> hasher,
    IClock clock)
{
    public const int MinPasswordLength = 10;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex CodePattern();

    public async Task<ProvisionTenantResponse> ProvisionAsync(ProvisionTenantRequest request, CancellationToken ct)
    {
        var code = (request.Code ?? "").Trim().ToLowerInvariant();
        if (!CodePattern().IsMatch(code))
            throw new BusinessRuleException("tenant.invalid_code",
                "Company code must be 3-40 characters: lowercase letters, digits and hyphens.",
                "يجب أن يتكون رمز الشركة من 3 إلى 40 حرفاً: أحرف إنجليزية صغيرة وأرقام وشرطات.");
        if (request.FiscalYearStartMonth is < 1 or > 12)
            throw new BusinessRuleException("tenant.invalid_fiscal_month", "Fiscal year start month must be 1-12.", "شهر بداية السنة المالية يجب أن يكون بين 1 و 12.");
        var email = Guard.Required(request.AdminEmail, "adminEmail").ToLowerInvariant();
        if ((request.AdminPassword ?? "").Length < MinPasswordLength)
            throw new BusinessRuleException("auth.weak_password", $"Password must be at least {MinPasswordLength} characters.",
                $"يجب ألا تقل كلمة المرور عن {MinPasswordLength} أحرف.");
        var language = request.PreferredLanguage is "en" ? "en" : "ar";

        if (await db.Set<Tenant>().AnyAsync(t => t.Code == code, ct))
            throw new ConflictException("tenant.code_taken", $"Company code '{code}' is already in use.", $"رمز الشركة '{code}' مستخدم مسبقاً.");

        var (nameEn, nameAr) = Bilingual.Require(request.NameEn, request.NameAr, "company name");
        var (adminEn, adminAr) = Bilingual.Require(request.AdminNameEn, request.AdminNameAr, "administrator name");
        var now = clock.UtcNow;

        var tenant = new Tenant { Code = code, NameEn = nameEn, NameAr = nameAr, DefaultLocale = $"{language}-QA", CreatedAt = now };
        var company = new Company { NameEn = nameEn, NameAr = nameAr, CrNo = request.CrNo?.Trim(), EstablishmentId = request.EstablishmentId?.Trim() };
        var branch = new Branch { CompanyId = company.Id, Code = "HO", NameEn = "Head Office", NameAr = "المكتب الرئيسي" };
        var admin = new AppUser
        {
            Email = email, DisplayNameEn = adminEn, DisplayNameAr = adminAr, PreferredLanguage = language, DefaultCompanyId = company.Id
        };
        admin.PasswordHash = hasher.HashPassword(admin, request.AdminPassword!);

        tenantContext.Set(tenant.Id, company.Id, admin.Id);

        await db.InTransactionAsync(async token =>
        {
            db.Add(tenant);
            await db.SaveChangesAsync(token);
            db.AddRange(company, branch, admin);

            var definitions = permissionProviders.SelectMany(p => p.GetPermissions()).ToList();
            foreach (var (roleCode, roleEn, roleAr) in DefaultRoleNames.All)
            {
                var role = new Role { Code = roleCode, NameEn = roleEn, NameAr = roleAr, IsSystem = true };
                IEnumerable<string> granted = roleCode == DefaultRoles.Owner
                    ? [DefaultRoles.Wildcard]
                    : definitions.Where(d => d.DefaultRoles.Contains(roleCode)).Select(d => d.Code).Distinct();
                role.Permissions.AddRange(granted.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
                db.Add(role);
                if (roleCode == DefaultRoles.Owner)
                    db.Add(new UserRole { UserId = admin.Id, RoleId = role.Id });
            }
            await db.SaveChangesAsync(token);

            var context = new TenantProvisioningContext(tenant.Id, company.Id, branch.Id, admin.Id, request.FiscalYearStartMonth, clock.Today);
            foreach (var provisioner in provisioners.OrderBy(p => p.Order))
            {
                await provisioner.ProvisionAsync(context, token);
                await db.SaveChangesAsync(token);
            }
        }, ct);

        return new ProvisionTenantResponse(tenant.Id, code, company.Id, branch.Id, admin.Id);
    }
}

using System.Security.Cryptography;
using System.Text;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Auth;
using Erp.Modules.Core.Domain;
using Erp.Modules.Core.Provisioning;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Core.Endpoints;

public sealed record BranchDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive);
public sealed record CreateBranchRequest(string Code, string? NameEn, string? NameAr);
public sealed record CompanyDto(Guid Id, string NameEn, string NameAr, string? CrNo, string? EstablishmentId, string? Tin,
    decimal ForeignOwnershipPct, string? AddressEn, string? AddressAr, string? Phone, string? Email, string BaseCurrency,
    string? ReceiptFooterEn, string? ReceiptFooterAr);
public sealed record UpdateCompanyRequest(string? NameEn, string? NameAr, string? CrNo, string? EstablishmentId, string? Tin,
    decimal ForeignOwnershipPct, string? AddressEn, string? AddressAr, string? Phone, string? Email,
    string? ReceiptFooterEn = null, string? ReceiptFooterAr = null);
public sealed record UserDto(Guid Id, string Email, string DisplayNameEn, string DisplayNameAr, string PreferredLanguage, bool IsActive,
    DateTimeOffset? LastLoginAt, IReadOnlyList<string> Roles);
public sealed record CreateUserRequest(string Email, string? DisplayNameEn, string? DisplayNameAr, string Password,
    string PreferredLanguage, IReadOnlyList<string> Roles);
public sealed record SetUserRolesRequest(IReadOnlyList<string> Roles);
public sealed record RoleDto(Guid Id, string Code, string NameEn, string NameAr, bool IsSystem, IReadOnlyList<string> Permissions);
public sealed record AuditDto(DateTimeOffset OccurredAt, Guid UserId, string EntityType, Guid EntityId, string Action, string Changes);

internal static class CoreEndpoints
{
    public const string PlatformKeyHeader = "X-Platform-Key";

    public static void Map(IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform").WithTags("Platform").AddEndpointFilter(RequirePlatformKey);
        platform.MapPost("/tenants", (ProvisionTenantRequest request, TenantProvisioningService service, CancellationToken ct) =>
            service.ProvisionAsync(request, ct));

        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        auth.MapPost("/login", (LoginRequest request, AuthService service, CancellationToken ct) => service.LoginAsync(request, ct))
            .AllowAnonymous();
        auth.MapPost("/mfa/verify", (MfaVerifyRequest request, AuthService service, CancellationToken ct) => service.VerifyMfaAsync(request, ct))
            .AllowAnonymous();
        auth.MapPost("/refresh", (RefreshRequest request, AuthService service, CancellationToken ct) => service.RefreshAsync(request, ct))
            .AllowAnonymous();
        auth.MapPost("/logout", async (RefreshRequest request, AuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(request, ct);
            return Results.NoContent();
        }).AllowAnonymous();
        auth.MapGet("/me", GetMe).RequireAuthorization();
        auth.MapPost("/password", async (ChangePasswordRequest request, AuthService service, CancellationToken ct) =>
        {
            await service.ChangePasswordAsync(request, ct);
            return Results.NoContent();
        }).RequireAuthorization();
        auth.MapGet("/mfa", (AuthService service, CancellationToken ct) => service.MfaStatusAsync(ct)).RequireAuthorization();
        auth.MapPost("/mfa/setup", (AuthService service, CancellationToken ct) => service.SetupMfaAsync(ct)).RequireAuthorization();
        auth.MapPost("/mfa/enable", (MfaCodeRequest request, AuthService service, CancellationToken ct) => service.EnableMfaAsync(request, ct))
            .RequireAuthorization();
        auth.MapPost("/mfa/disable", (MfaCodeRequest request, AuthService service, CancellationToken ct) => service.DisableMfaAsync(request, ct))
            .RequireAuthorization();

        var core = app.MapGroup("/api/core").WithTags("Core").RequireAuthorization();
        core.MapGet("/company", GetCompany);
        core.MapPut("/company", UpdateCompany).RequirePermission(CorePermissions.CompanyManage);
        core.MapGet("/branches", ListBranches);
        core.MapPost("/branches", CreateBranch).RequirePermission(CorePermissions.BranchesManage);
        core.MapGet("/users", ListUsers).RequirePermission(CorePermissions.UsersRead);
        core.MapPost("/users", CreateUser).RequirePermission(CorePermissions.UsersManage);
        core.MapPut("/users/{id:guid}/roles", SetUserRoles).RequirePermission(CorePermissions.RolesManage);
        core.MapPost("/users/{id:guid}/deactivate", DeactivateUser).RequirePermission(CorePermissions.UsersManage);
        core.MapGet("/roles", ListRoles).RequirePermission(CorePermissions.UsersRead);
        core.MapGet("/permissions", (IEnumerable<IPermissionProvider> providers) =>
            providers.SelectMany(p => p.GetPermissions()).Select(p => new { p.Code, p.DescriptionEn, p.DescriptionAr }).OrderBy(p => p.Code));
        core.MapGet("/audit", ListAudit).RequirePermission(CorePermissions.AuditRead);
    }

    internal static ValueTask<object?> RequirePlatformKeyFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        RequirePlatformKey(context, next);

    private static async ValueTask<object?> RequirePlatformKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configured = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Platform:ApiKey"];
        var supplied = context.HttpContext.Request.Headers[PlatformKeyHeader].ToString();
        if (string.IsNullOrEmpty(configured) || supplied.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(supplied)))
            return Results.Unauthorized();
        return await next(context);
    }

    private static async Task<UserInfo> GetMe(ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var user = await db.Set<AppUser>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == tenant.UserId, ct)
            ?? throw new NotFoundException("User", tenant.UserId!);
        return AuthService.ToInfo(user, tenant.CompanyId);
    }

    private static async Task<CompanyDto> GetCompany(ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var c = await db.Set<Company>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenant.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Company", tenant.RequireCompanyId());
        return ToDto(c);
    }

    private static async Task<CompanyDto> UpdateCompany(UpdateCompanyRequest request, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var c = await db.Set<Company>().SingleOrDefaultAsync(x => x.Id == tenant.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Company", tenant.RequireCompanyId());
        (c.NameEn, c.NameAr) = Bilingual.Require(request.NameEn, request.NameAr, "company name");
        if (request.ForeignOwnershipPct is < 0 or > 100)
            throw new BusinessRuleException("company.invalid_ownership", "Foreign ownership must be between 0 and 100%.", "نسبة الملكية الأجنبية يجب أن تكون بين 0 و 100%.");
        c.CrNo = request.CrNo?.Trim();
        c.EstablishmentId = request.EstablishmentId?.Trim();
        c.Tin = request.Tin?.Trim();
        c.ForeignOwnershipPct = request.ForeignOwnershipPct;
        c.AddressEn = request.AddressEn;
        c.AddressAr = request.AddressAr;
        c.Phone = request.Phone;
        c.Email = request.Email;
        c.ReceiptFooterEn = request.ReceiptFooterEn?.Trim();
        c.ReceiptFooterAr = request.ReceiptFooterAr?.Trim();
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    private static CompanyDto ToDto(Company c) => new(c.Id, c.NameEn, c.NameAr, c.CrNo, c.EstablishmentId, c.Tin, c.ForeignOwnershipPct,
        c.AddressEn, c.AddressAr, c.Phone, c.Email, c.BaseCurrency, c.ReceiptFooterEn, c.ReceiptFooterAr);

    private static async Task<List<BranchDto>> ListBranches(ErpDbContext db, ITenantContext tenant, CancellationToken ct) =>
        await db.Set<Branch>().AsNoTracking()
            .Where(b => b.CompanyId == tenant.RequireCompanyId())
            .OrderBy(b => b.Code)
            .Select(b => new BranchDto(b.Id, b.Code, b.NameEn, b.NameAr, b.IsActive))
            .ToListAsync(ct);

    private static async Task<BranchDto> CreateBranch(CreateBranchRequest request, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(request.NameEn, request.NameAr);
        var branch = new Branch { CompanyId = tenant.RequireCompanyId(), Code = Guard.Required(request.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar };
        db.Add(branch);
        await db.SaveChangesAsync(ct);
        return new BranchDto(branch.Id, branch.Code, branch.NameEn, branch.NameAr, branch.IsActive);
    }

    private static async Task<List<UserDto>> ListUsers(ErpDbContext db, CancellationToken ct)
    {
        var users = await db.Set<AppUser>().AsNoTracking().OrderBy(u => u.Email ?? u.Username).ToListAsync(ct);
        var roles = await (from ur in db.Set<UserRole>()
                           join r in db.Set<Role>() on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Code }).ToListAsync(ct);
        return users.Select(u => new UserDto(u.Id, u.Email ?? u.Username ?? "", u.DisplayNameEn, u.DisplayNameAr, u.PreferredLanguage, u.IsActive, u.LastLoginAt,
            roles.Where(r => r.UserId == u.Id).Select(r => r.Code).Order().ToList())).ToList();
    }

    private static async Task<UserDto> CreateUser(CreateUserRequest request, ErpDbContext db, ITenantContext tenant,
        IPasswordHasher<AppUser> hasher, CancellationToken ct)
    {
        if ((request.Password ?? "").Length < TenantProvisioningService.MinPasswordLength)
            throw new BusinessRuleException("auth.weak_password", $"Password must be at least {TenantProvisioningService.MinPasswordLength} characters.",
                $"يجب ألا تقل كلمة المرور عن {TenantProvisioningService.MinPasswordLength} أحرف.");
        var (en, ar) = Bilingual.Require(request.DisplayNameEn, request.DisplayNameAr, "display name");
        var user = new AppUser
        {
            Email = Guard.Required(request.Email, "email").ToLowerInvariant(),
            DisplayNameEn = en,
            DisplayNameAr = ar,
            PreferredLanguage = request.PreferredLanguage is "en" ? "en" : "ar",
            DefaultCompanyId = tenant.CompanyId
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        db.Add(user);
        var roleIds = await ResolveRolesAsync(db, request.Roles, ct);
        db.AddRange(roleIds.Select(r => new UserRole { UserId = user.Id, RoleId = r.Id }));
        await db.SaveChangesAsync(ct);
        return new UserDto(user.Id, user.Email!, user.DisplayNameEn, user.DisplayNameAr, user.PreferredLanguage, true, null,
            roleIds.Select(r => r.Code).Order().ToList());
    }

    private static async Task<IResult> SetUserRoles(Guid id, SetUserRolesRequest request, ErpDbContext db, ITenantContext tenant,
        IMemoryCache cache, CancellationToken ct)
    {
        if (!await db.Set<AppUser>().AnyAsync(u => u.Id == id, ct)) throw new NotFoundException("User", id);
        var roles = await ResolveRolesAsync(db, request.Roles, ct);
        var existing = await db.Set<UserRole>().Where(ur => ur.UserId == id).ToListAsync(ct);
        db.RemoveRange(existing.Where(e => roles.All(r => r.Id != e.RoleId)));
        db.AddRange(roles.Where(r => existing.All(e => e.RoleId != r.Id)).Select(r => new UserRole { UserId = id, RoleId = r.Id }));
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(cache, tenant.RequireTenantId(), id);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateUser(Guid id, ErpDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        if (id == tenant.UserId)
            throw new BusinessRuleException("user.cannot_deactivate_self", "You cannot deactivate your own account.", "لا يمكنك تعطيل حسابك.");
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
        user.IsActive = false;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<List<Role>> ResolveRolesAsync(ErpDbContext db, IReadOnlyList<string>? codes, CancellationToken ct)
    {
        var wanted = (codes ?? []).Select(c => c.Trim().ToLowerInvariant()).Distinct().ToList();
        var roles = await db.Set<Role>().Where(r => wanted.Contains(r.Code)).ToListAsync(ct);
        var missing = wanted.Except(roles.Select(r => r.Code)).ToList();
        if (missing.Count > 0)
            throw new BusinessRuleException("role.unknown", $"Unknown role(s): {string.Join(", ", missing)}", "دور غير معروف.");
        return roles;
    }

    private static async Task<List<RoleDto>> ListRoles(ErpDbContext db, CancellationToken ct) =>
        (await db.Set<Role>().AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Code).ToListAsync(ct))
        .Select(r => new RoleDto(r.Id, r.Code, r.NameEn, r.NameAr, r.IsSystem, r.Permissions.Select(p => p.Permission).Order().ToList()))
        .ToList();

    private static async Task<List<AuditDto>> ListAudit(string? entityType, Guid? entityId, ErpDbContext db, CancellationToken ct, int take = 100)
    {
        var query = db.Set<AuditLogEntry>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (entityId is not null) query = query.Where(a => a.EntityId == entityId);
        return await query.OrderByDescending(a => a.OccurredAt).Take(Math.Clamp(take, 1, 500))
            .Select(a => new AuditDto(a.OccurredAt, a.UserId, a.EntityType, a.EntityId, a.Action, a.Changes))
            .ToListAsync(ct);
    }
}

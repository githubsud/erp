using System.Security.Claims;
using System.Text;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Tenancy;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Modules.Core.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "erp";
    public string Audience { get; set; } = "erp";

    /// <summary>HMAC key (min 32 bytes). Production should move to asymmetric keys in Key Vault.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 60;

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record LoginRequest(string TenantCode, string Email, string Password);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserInfo User, IReadOnlyCollection<string> Permissions);

public sealed record UserInfo(Guid Id, Guid TenantId, Guid? CompanyId, string Email, string DisplayNameEn, string DisplayNameAr, string PreferredLanguage);

public sealed class AuthService(
    ErpDbContext db,
    ITenantContext tenantContext,
    IPermissionService permissions,
    IPasswordHasher<AppUser> hasher,
    JwtOptions jwt,
    IClock clock)
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var invalid = new BusinessRuleException("auth.invalid_credentials", "Company code, email or password is incorrect.",
            "رمز الشركة أو البريد الإلكتروني أو كلمة المرور غير صحيحة.");

        var code = (request.TenantCode ?? "").Trim().ToLowerInvariant();
        var tenant = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.Code == code, ct);
        if (tenant is null || tenant.Status != TenantStatus.Active) throw invalid;

        // From here on every query is scoped to this tenant (filters + row-level security).
        tenantContext.Set(tenant.Id, null, null);

        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !user.IsActive)
        {
            hasher.HashPassword(new AppUser { Email = email, DisplayNameEn = "", DisplayNameAr = "" }, request.Password ?? "");
            throw invalid;
        }

        var now = clock.UtcNow;
        if (user.LockoutUntil > now)
            throw new BusinessRuleException("auth.locked_out", "Too many failed attempts. Try again later.",
                "محاولات فاشلة كثيرة. حاول مرة أخرى لاحقاً.");

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "");
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockoutUntil = now.Add(LockoutDuration);
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            throw invalid;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password!);

        var companyId = user.DefaultCompanyId
            ?? await db.Set<Company>().OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        tenantContext.Set(tenant.Id, companyId, user.Id);
        await db.SaveChangesAsync(ct);

        var expires = now.AddMinutes(jwt.AccessTokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Expires = expires.UtcDateTime,
            NotBefore = now.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.CreateKey(), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ErpClaims.Tenant, tenant.Id.ToString()),
                new Claim(ErpClaims.Company, companyId?.ToString() ?? ""),
                new Claim(ErpClaims.Language, user.PreferredLanguage)
            ])
        });

        var granted = await permissions.GetPermissionsAsync(user.Id, ct);
        return new LoginResponse(token, expires, ToInfo(user, companyId), granted.Order().ToList());
    }

    public static UserInfo ToInfo(AppUser user, Guid? companyId) =>
        new(user.Id, user.TenantId, companyId, user.Email, user.DisplayNameEn, user.DisplayNameAr, user.PreferredLanguage);
}

/// <summary>Loads a user's permissions from their roles. Cached briefly; role edits take effect within a minute.</summary>
public sealed class PermissionService(ErpDbContext db, ITenantContext tenant, IMemoryCache cache) : IPermissionService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var key = $"perm:{tenant.RequireTenantId()}:{userId}";
        if (cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null) return cached;

        var granted = await (
                from ur in db.Set<UserRole>()
                join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                where ur.UserId == userId
                select rp.Permission)
            .Distinct()
            .ToListAsync(ct);

        IReadOnlySet<string> result = granted.ToHashSet(StringComparer.Ordinal);
        cache.Set(key, result, CacheDuration);
        return result;
    }

    public async Task<bool> HasAsync(string permission, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } userId) return false;
        var granted = await GetPermissionsAsync(userId, ct);
        return granted.Contains(DefaultRoles.Wildcard) || granted.Contains(permission);
    }

    public static void Invalidate(IMemoryCache cache, Guid tenantId, Guid userId) => cache.Remove($"perm:{tenantId}:{userId}");
}

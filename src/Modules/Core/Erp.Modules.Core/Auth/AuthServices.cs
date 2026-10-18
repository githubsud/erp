using System.Security.Claims;
using System.Security.Cryptography;
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

    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
    public int MfaChallengeMinutes { get; set; } = 5;

    /// <summary>The MFA challenge token uses its own audience so it can never be used as an access token.</summary>
    public string MfaAudience => Audience + "-mfa";

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        return new SymmetricSecurityKey(bytes);
    }
}

/// <summary><see cref="Email"/> may also be a username (employees sign in with their employee number).</summary>
public sealed record LoginRequest(string TenantCode, string Email, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record MfaVerifyRequest(string MfaToken, string Code);

public sealed record RefreshRequest(string RefreshToken);

public sealed record MfaCodeRequest(string Code);

public sealed record MfaStatus(bool Enabled);

public sealed record MfaSetup(string Secret, string OtpAuthUri);

/// <summary>
/// Either a full session (access + refresh token) or, when the user has two-factor sign-in enabled,
/// a short-lived challenge to complete with an authenticator code.
/// </summary>
public sealed record LoginResponse(
    string? AccessToken,
    DateTimeOffset? ExpiresAt,
    UserInfo? User,
    IReadOnlyCollection<string>? Permissions,
    string? RefreshToken = null,
    bool MfaRequired = false,
    string? MfaToken = null,
    bool MfaEnabled = false,
    bool MustChangePassword = false);

public sealed record UserInfo(Guid Id, Guid TenantId, Guid? CompanyId, string? Email, string DisplayNameEn, string DisplayNameAr, string PreferredLanguage,
    string? Username = null);

public sealed class AuthService(
    ErpDbContext db,
    ITenantContext tenantContext,
    IPermissionService permissions,
    IPasswordHasher<AppUser> hasher,
    JwtOptions jwt,
    IClock clock)
{
    private const int MaxFailedAttempts = 5;
    private const string Issuer = "Qatar SME ERP";
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private static BusinessRuleException InvalidCredentials() => new("auth.invalid_credentials",
        "Company code, email or password is incorrect.", "رمز الشركة أو البريد الإلكتروني أو كلمة المرور غير صحيحة.");

    private static BusinessRuleException InvalidSession() => new("auth.session_expired",
        "Your session has expired. Please sign in again.", "انتهت الجلسة. يرجى تسجيل الدخول مرة أخرى.");

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var code = (request.TenantCode ?? "").Trim().ToLowerInvariant();
        var tenant = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.Code == code, ct);
        if (tenant is null || tenant.Status != TenantStatus.Active) throw InvalidCredentials();

        // From here on every query is scoped to this tenant (filters + row-level security).
        tenantContext.Set(tenant.Id, null, null);

        var login = (request.Email ?? "").Trim().ToLowerInvariant();
        var user = login.Contains('@')
            ? await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Email == login, ct)
            : await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Username == login, ct);
        if (user is null || !user.IsActive)
        {
            hasher.HashPassword(new AppUser { Email = login, DisplayNameEn = "", DisplayNameAr = "" }, request.Password ?? "");
            throw InvalidCredentials();
        }

        EnsureNotLockedOut(user);
        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "");
        if (verification == PasswordVerificationResult.Failed)
        {
            await RegisterFailureAsync(user, ct);
            throw InvalidCredentials();
        }
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password!);

        if (user.MfaEnabled)
        {
            await db.SaveChangesAsync(ct);
            return new LoginResponse(null, null, null, null, MfaRequired: true, MfaToken: CreateMfaChallenge(tenant.Id, user.Id), MfaEnabled: true);
        }

        return await StartSessionAsync(tenant.Id, user, ct);
    }

    public async Task<LoginResponse> VerifyMfaAsync(MfaVerifyRequest request, CancellationToken ct)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(request.MfaToken, new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.MfaAudience,
            IssuerSigningKey = jwt.CreateKey(),
            ClockSkew = TimeSpan.FromSeconds(10)
        });
        string? Claim(string name) => result.Claims.TryGetValue(name, out var value) ? value?.ToString() : null;
        if (!result.IsValid || !Guid.TryParse(Claim(ErpClaims.Tenant), out var tenantId) || !Guid.TryParse(Claim(JwtRegisteredClaimNames.Sub), out var userId))
            throw InvalidSession();

        tenantContext.Set(tenantId, null, null);
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive && u.MfaEnabled, ct) ?? throw InvalidSession();
        EnsureNotLockedOut(user);
        var step = user.MfaSecret is null ? null : Totp.Verify(user.MfaSecret, request.Code, clock.UtcNow, user.MfaLastStep);
        if (step is null)
        {
            await RegisterFailureAsync(user, ct);
            throw new BusinessRuleException("auth.invalid_code", "The verification code is not correct.", "رمز التحقق غير صحيح.");
        }
        user.MfaLastStep = step.Value;
        return await StartSessionAsync(tenantId, user, ct);
    }

    /// <summary>Rotates the refresh token. Re-use of an already rotated token revokes every session of the user.</summary>
    public async Task<LoginResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var token = request.RefreshToken ?? "";
        var dot = token.IndexOf('.');
        if (dot <= 0 || !Guid.TryParseExact(token[..dot], "N", out var tenantId)) throw InvalidSession();
        tenantContext.Set(tenantId, null, null);

        var hash = Hash(token);
        var stored = await db.Set<RefreshToken>().SingleOrDefaultAsync(r => r.TokenHash == hash, ct) ?? throw InvalidSession();
        var now = clock.UtcNow;
        if (stored.RevokedAt is not null)
        {
            if (stored.ReplacedBy is not null)
                await RevokeAllAsync(stored.UserId, now, ct);
            throw InvalidSession();
        }
        if (stored.ExpiresAt <= now) throw InvalidSession();

        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == stored.UserId && u.IsActive, ct) ?? throw InvalidSession();
        var session = await StartSessionAsync(tenantId, user, ct, stored.CompanyId, replacing: stored);
        return session;
    }

    public async Task LogoutAsync(RefreshRequest request, CancellationToken ct)
    {
        var token = request.RefreshToken ?? "";
        var dot = token.IndexOf('.');
        if (dot <= 0 || !Guid.TryParseExact(token[..dot], "N", out var tenantId)) return;
        tenantContext.Set(tenantId, null, null);
        var hash = Hash(token);
        var stored = await db.Set<RefreshToken>().SingleOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // Two-factor management for the signed-in user ------------------------------------------------------

    public async Task<MfaStatus> MfaStatusAsync(CancellationToken ct) => new((await CurrentUserAsync(ct)).MfaEnabled);

    public async Task<MfaSetup> SetupMfaAsync(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user.MfaEnabled)
            throw new BusinessRuleException("auth.mfa_already_enabled", "Two-factor sign-in is already enabled.", "التحقق بخطوتين مفعّل مسبقاً.");
        var secret = Totp.NewSecret();
        user.MfaPendingSecret = secret;
        await db.SaveChangesAsync(ct);
        return new MfaSetup(secret, Totp.ProvisioningUri(secret, user.Email ?? user.Username ?? user.Id.ToString(), Issuer));
    }

    public async Task<MfaStatus> EnableMfaAsync(MfaCodeRequest request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var step = user.MfaPendingSecret is null ? null : Totp.Verify(user.MfaPendingSecret, request.Code, clock.UtcNow, user.MfaLastStep);
        if (step is null)
            throw new BusinessRuleException("auth.invalid_code", "The verification code is not correct.", "رمز التحقق غير صحيح.");
        user.MfaSecret = user.MfaPendingSecret;
        user.MfaPendingSecret = null;
        user.MfaEnabled = true;
        user.MfaLastStep = step.Value;
        await db.SaveChangesAsync(ct);
        return new MfaStatus(true);
    }

    public async Task<MfaStatus> DisableMfaAsync(MfaCodeRequest request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var step = user.MfaSecret is null ? null : Totp.Verify(user.MfaSecret, request.Code, clock.UtcNow, user.MfaLastStep);
        if (!user.MfaEnabled || step is null)
            throw new BusinessRuleException("auth.invalid_code", "The verification code is not correct.", "رمز التحقق غير صحيح.");
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaLastStep = step.Value;
        await db.SaveChangesAsync(ct);
        return new MfaStatus(false);
    }

    /// <summary>Replaces the signed-in user's password (and clears a temporary password).</summary>
    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
            throw new BusinessRuleException("auth.wrong_password", "The current password is not correct.", "كلمة المرور الحالية غير صحيحة.");
        var next = request.NewPassword ?? "";
        if (next.Length < Provisioning.TenantProvisioningService.MinPasswordLength)
            throw new BusinessRuleException("auth.weak_password", $"Password must be at least {Provisioning.TenantProvisioningService.MinPasswordLength} characters.",
                $"يجب ألا تقل كلمة المرور عن {Provisioning.TenantProvisioningService.MinPasswordLength} أحرف.");
        if (next == request.CurrentPassword)
            throw new BusinessRuleException("auth.same_password", "Choose a password different from the current one.", "اختر كلمة مرور مختلفة عن الحالية.");
        user.PasswordHash = hasher.HashPassword(user, next);
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);
    }

    public static UserInfo ToInfo(AppUser user, Guid? companyId) =>
        new(user.Id, user.TenantId, companyId, user.Email, user.DisplayNameEn, user.DisplayNameAr, user.PreferredLanguage, user.Username);

    // ----------------------------------------------------------------------------------------------------

    private async Task<LoginResponse> StartSessionAsync(Guid tenantId, AppUser user, CancellationToken ct, Guid? companyId = null,
        RefreshToken? replacing = null)
    {
        var now = clock.UtcNow;
        companyId ??= user.DefaultCompanyId
                      ?? await db.Set<Company>().OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        tenantContext.Set(tenantId, companyId, user.Id);

        var refreshValue = $"{tenantId:N}.{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))}";
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refreshValue),
            CompanyId = companyId,
            ExpiresAt = now.AddDays(jwt.RefreshTokenDays)
        };
        db.Add(refresh);
        if (replacing is not null)
        {
            replacing.RevokedAt = now;
            replacing.ReplacedBy = refresh.Id;
        }
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
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
                new Claim(ErpClaims.Tenant, tenantId.ToString()),
                new Claim(ErpClaims.Company, companyId?.ToString() ?? ""),
                new Claim(ErpClaims.Language, user.PreferredLanguage)
            ])
        });

        var granted = await permissions.GetPermissionsAsync(user.Id, ct);
        return new LoginResponse(token, expires, ToInfo(user, companyId), granted.Order().ToList(), refreshValue, MfaEnabled: user.MfaEnabled,
            MustChangePassword: user.MustChangePassword);
    }

    private string CreateMfaChallenge(Guid tenantId, Guid userId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.MfaAudience,
            Expires = clock.UtcNow.AddMinutes(jwt.MfaChallengeMinutes).UtcDateTime,
            NotBefore = clock.UtcNow.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.CreateKey(), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim(ErpClaims.Tenant, tenantId.ToString())])
        });

    private void EnsureNotLockedOut(AppUser user)
    {
        if (user.LockoutUntil > clock.UtcNow)
            throw new BusinessRuleException("auth.locked_out", "Too many failed attempts. Try again later.", "محاولات فاشلة كثيرة. حاول مرة أخرى لاحقاً.");
    }

    private async Task RegisterFailureAsync(AppUser user, CancellationToken ct)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= MaxFailedAttempts)
        {
            user.LockoutUntil = clock.UtcNow.Add(LockoutDuration);
            user.FailedLoginCount = 0;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeAllAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Set<RefreshToken>().Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now).SetProperty(r => r.UpdatedAt, now), ct);

    private async Task<AppUser> CurrentUserAsync(CancellationToken ct) =>
        await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == tenantContext.UserId, ct) ?? throw new ForbiddenException();

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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

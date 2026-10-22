using System.Security.Cryptography;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Core.Auth;

/// <summary>Sign-ins for people who are not staff users (employees using self-service), created on behalf of other modules.</summary>
internal sealed class UserAccounts(ErpDbContext db, ITenantContext tenant, IPasswordHasher<AppUser> hasher) : IUserAccounts
{
    // No 0/O, 1/l/I: temporary passwords are read out or printed.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    private const int TemporaryPasswordLength = 10;

    public async Task<PortalAccount> CreateOrResetAsync(Guid? existingUserId, string username, string? email, string nameEn, string nameAr, string language,
        string roleCode, CancellationToken ct)
    {
        var login = Guard.Required(username, "username").Trim().ToLowerInvariant();
        var password = TemporaryPassword();
        var user = existingUserId is { } id ? await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == id, ct) : null;
        if (user is null && await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Username == login, ct) is { } taken)
        {
            // A portal sign-in that was revoked earlier comes back to life; anything else with this username is someone else's.
            if (taken.IsActive || taken.Email is not null)
                throw new ConflictException("auth.username_taken", $"The username {login} is already used.", $"اسم المستخدم {login} مستخدم مسبقاً.");
            user = taken;
        }
        // Resetting hands a new password to whoever asked: never for an account that has since been given more than this portal role.
        var role = await db.Set<Role>().SingleOrDefaultAsync(r => r.Code == roleCode, ct) ?? throw new NotFoundException("Role", roleCode);
        if (user is not null && await db.Set<UserRole>().AnyAsync(r => r.UserId == user.Id && r.RoleId != role.Id, ct))
            throw new ConflictException("auth.portal_is_staff", "This sign-in has other access in the system too. The owner resets it from the users screen.",
                "حساب الدخول هذا له صلاحيات أخرى في النظام. يعيد المالك تعيينه من شاشة المستخدمين.");
        if (user is null)
        {
            var mail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
            // Staff may already use this email; the employee then signs in with the username only.
            if (mail is not null && await db.Set<AppUser>().AnyAsync(u => u.Email == mail, ct)) mail = null;
            user = new AppUser
            {
                Username = login, Email = mail, DisplayNameEn = nameEn, DisplayNameAr = nameAr, PreferredLanguage = language is "en" ? "en" : "ar",
                DefaultCompanyId = tenant.CompanyId
            };
            db.Add(user);
        }
        user.PasswordHash = hasher.HashPassword(user, password);
        user.MustChangePassword = true;
        user.IsActive = true;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;

        if (!await db.Set<UserRole>().AnyAsync(r => r.UserId == user.Id && r.RoleId == role.Id, ct))
            db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync(ct);
        return new PortalAccount(user.Id, user.Username ?? login, password);
    }

    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken ct)
    {
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User", userId);
        user.IsActive = active;
        if (!active)
            foreach (var token in await db.Set<RefreshToken>().Where(r => r.UserId == userId && r.RevokedAt == null).ToListAsync(ct))
                token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string TemporaryPassword() =>
        new(Enumerable.Range(0, TemporaryPasswordLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}

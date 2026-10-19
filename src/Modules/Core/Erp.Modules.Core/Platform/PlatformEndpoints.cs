using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Auth;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Erp.Modules.Core.Platform;

public sealed record ModuleDto(string Code, string NameEn, string NameAr, bool Enabled, IReadOnlyList<string> Requires);

public sealed record ModuleSwitchRequest(IReadOnlyList<string> Disabled);

public sealed record AuditRowDto(DateTimeOffset OccurredAt, Guid UserId, string? UserName, string EntityType, Guid EntityId, string Action, string Changes);

public sealed record AuditPage(IReadOnlyList<AuditRowDto> Items, int Total, int Page, int PageSize, IReadOnlyList<string> EntityTypes);

public sealed record AccessReviewRow(Guid UserId, string Login, string DisplayNameEn, string DisplayNameAr, bool IsActive, IReadOnlyList<string> Roles,
    int PermissionCount, bool IsOwner, bool MfaEnabled, DateTimeOffset? LastLoginAt, IReadOnlyList<string> Flags);

/// <summary>Switched-off modules of the current company, cached briefly (switching takes effect within a minute everywhere).</summary>
internal sealed class ModuleAccess(ErpDbContext db, ITenantContext tenant, IMemoryCache cache) : IModuleAccess
{
    public async Task<IReadOnlySet<string>> DisabledAsync(CancellationToken ct)
    {
        if (tenant.CompanyId is not { } companyId) return new HashSet<string>();
        var key = $"modules:{companyId}";
        if (cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null) return cached;
        var raw = await db.Set<Company>().AsNoTracking().Where(c => c.Id == companyId).Select(c => c.DisabledModules).SingleOrDefaultAsync(ct) ?? "";
        IReadOnlySet<string> result = Parse(raw);
        cache.Set(key, result, TimeSpan.FromSeconds(30));
        return result;
    }

    internal static HashSet<string> Parse(string raw) => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    internal static void Invalidate(IMemoryCache cache, Guid companyId) => cache.Remove($"modules:{companyId}");
}

internal static class PlatformEndpoints
{
    private static readonly (string Code, string En, string Ar, string[] Requires)[] Catalog =
    [
        (ModuleCodes.Hr, "HR & Payroll", "الموارد البشرية والرواتب", []),
        (ModuleCodes.Inventory, "Inventory", "المخزون", []),
        (ModuleCodes.Purchasing, "Purchasing", "المشتريات", [ModuleCodes.Inventory]),
        (ModuleCodes.Sales, "Sales", "المبيعات", [ModuleCodes.Inventory])
    ];

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/core").WithTags("Core").RequireAuthorization();
        g.MapGet("/modules", ListModules);
        g.MapPut("/modules", SaveModules).RequirePermission(CorePermissions.CompanyManage);
        g.MapGet("/audit-log", AuditAsync).RequirePermission(CorePermissions.AuditRead);
        g.MapGet("/access-review", AccessReviewAsync).RequirePermission(CorePermissions.UsersRead);
    }

    private static async Task<List<ModuleDto>> ListModules(IModuleAccess access, CancellationToken ct)
    {
        var disabled = await access.DisabledAsync(ct);
        return Catalog.Select(m => new ModuleDto(m.Code, m.En, m.Ar, !disabled.Contains(m.Code), m.Requires)).ToList();
    }

    private static async Task<List<ModuleDto>> SaveModules(ModuleSwitchRequest r, ErpDbContext db, ITenantContext t, IMemoryCache cache, CancellationToken ct)
    {
        var disabled = (r.Disabled ?? []).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
        if (disabled.Any(d => !ModuleCodes.Optional.Contains(d)))
            throw new BusinessRuleException("core.unknown_module", "Unknown module.", "وحدة غير معروفة.");
        foreach (var m in Catalog.Where(m => !disabled.Contains(m.Code)))
            foreach (var required in m.Requires.Where(disabled.Contains))
                throw new BusinessRuleException("core.module_required", $"{m.En} needs {required}; switch {m.En} off first.",
                    $"تحتاج وحدة {m.Ar} إلى وحدة أخرى مفعّلة؛ أوقف {m.Ar} أولاً.");
        var company = await db.Set<Company>().SingleAsync(c => c.Id == t.RequireCompanyId(), ct);
        company.DisabledModules = string.Join(',', disabled.Order());
        await db.SaveChangesAsync(ct);
        ModuleAccess.Invalidate(cache, company.Id);
        var set = disabled.ToHashSet();
        return Catalog.Select(m => new ModuleDto(m.Code, m.En, m.Ar, !set.Contains(m.Code), m.Requires)).ToList();
    }

    /// <summary>Audit trail with filters; newest first.</summary>
    private static async Task<AuditPage> AuditAsync(ErpDbContext db, CancellationToken ct, string? entityType = null, Guid? entityId = null,
        Guid? userId = null, string? action = null, DateOnly? from = null, DateOnly? to = null, int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.Set<AuditLogEntry>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (entityId is not null) q = q.Where(a => a.EntityId == entityId);
        if (userId is not null) q = q.Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (from is { } f) q = q.Where(a => a.OccurredAt >= new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)));
        if (to is { } tt) q = q.Where(a => a.OccurredAt < new DateTimeOffset(tt.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.OccurredAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayNameEn, ct);
        var types = await db.Set<AuditLogEntry>().AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(x => x).ToListAsync(ct);
        return new AuditPage(rows.Select(a => new AuditRowDto(a.OccurredAt, a.UserId, names.GetValueOrDefault(a.UserId), a.EntityType, a.EntityId, a.Action,
            a.Changes)).ToList(), total, page, pageSize, types);
    }

    /// <summary>Who can do what: roles, owner rights, two-factor sign-in and dormant accounts, for a periodic access review.</summary>
    private static async Task<List<AccessReviewRow>> AccessReviewAsync(ErpDbContext db, IClock clock, CancellationToken ct)
    {
        var users = await db.Set<AppUser>().AsNoTracking().OrderBy(u => u.DisplayNameEn).ToListAsync(ct);
        var roles = await (from ur in db.Set<UserRole>().AsNoTracking()
                           join r in db.Set<Role>() on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Id, r.Code }).ToListAsync(ct);
        var permissions = await db.Set<RolePermission>().AsNoTracking().Select(p => new { p.RoleId, p.Permission }).ToListAsync(ct);
        var dormantBefore = clock.UtcNow.AddDays(-90);
        return users.Select(u =>
        {
            var mine = roles.Where(r => r.UserId == u.Id).ToList();
            var granted = permissions.Where(p => mine.Any(r => r.Id == p.RoleId)).Select(p => p.Permission).Distinct().ToList();
            var owner = granted.Contains(DefaultRoles.Wildcard);
            var employeeOnly = granted.Count > 0 && granted.All(p => p.StartsWith("ess.", StringComparison.Ordinal));
            var flags = new List<string>();
            if (u.IsActive && (u.LastLoginAt is null || u.LastLoginAt < dormantBefore)) flags.Add("dormant");
            if (u.IsActive && !u.MfaEnabled && !employeeOnly && (owner || granted.Any(p => p.EndsWith(":post", StringComparison.Ordinal) || p.EndsWith(":approve", StringComparison.Ordinal))))
                flags.Add("no_mfa");
            if (u.IsActive && mine.Count == 0) flags.Add("no_role");
            return new AccessReviewRow(u.Id, u.Email ?? u.Username ?? "", u.DisplayNameEn, u.DisplayNameAr, u.IsActive, mine.Select(r => r.Code).Order().ToList(),
                granted.Count, owner, u.MfaEnabled, u.LastLoginAt, flags);
        }).ToList();
    }
}

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

public sealed record ModuleDto(string Code, string NameEn, string NameAr, bool Enabled, IReadOnlyList<string> Requires, bool Licensed = true);

public sealed record ModuleSwitchRequest(IReadOnlyList<string> Disabled);

public sealed record AuditRowDto(DateTimeOffset OccurredAt, Guid UserId, string? UserName, string EntityType, Guid EntityId, string Action, string Changes);

public sealed record AuditPage(IReadOnlyList<AuditRowDto> Items, int Total, int Page, int PageSize, IReadOnlyList<string> EntityTypes);

public sealed record AccessReviewRow(Guid UserId, string Login, string DisplayNameEn, string DisplayNameAr, bool IsActive, IReadOnlyList<string> Roles,
    int PermissionCount, bool IsOwner, bool MfaEnabled, DateTimeOffset? LastLoginAt, IReadOnlyList<string> Flags);

/// <summary>
/// Modules that are off for the current company: not in the customer's subscription, or switched off by the company. Cached briefly,
/// so licence and switch changes take effect within a minute everywhere.
/// </summary>
internal sealed class ModuleAccess(ErpDbContext db, ITenantContext tenant, IMemoryCache cache) : IModuleAccess
{
    private sealed record State(IReadOnlySet<string> Licensed, IReadOnlySet<string> Disabled, bool Suspended);

    public async Task<IReadOnlySet<string>> DisabledAsync(CancellationToken ct) => (await StateAsync(ct)).Disabled;

    public async Task<IReadOnlySet<string>> LicensedAsync(CancellationToken ct) => (await StateAsync(ct)).Licensed;

    public async Task<bool> SuspendedAsync(CancellationToken ct) => (await StateAsync(ct)).Suspended;

    private async Task<State> StateAsync(CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId || tenant.CompanyId is not { } companyId)
            return new State(ModuleCodes.Optional.ToHashSet(), new HashSet<string>(), false);
        var version = cache.GetOrCreate($"modules-ver:{tenantId}", e => Guid.NewGuid());
        var key = $"modules:{companyId}:{version}";
        if (cache.TryGetValue(key, out State? cached) && cached is not null) return cached;
        var switchedOff = ModuleCodes.Parse(await db.Set<Company>().AsNoTracking().Where(c => c.Id == companyId).Select(c => c.DisabledModules)
            .SingleOrDefaultAsync(ct));
        var subscription = await db.Set<Subscription>().AsNoTracking().Where(s => s.TenantId == tenantId)
            .Select(s => new { s.Modules, s.Status }).SingleOrDefaultAsync(ct);
        var status = await db.Set<Tenant>().AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Status).SingleOrDefaultAsync(ct);
        // Tenants created before billing existed have no subscription row: everything stays licensed.
        IReadOnlySet<string> licensed = subscription is null ? ModuleCodes.Optional.ToHashSet() : ModuleCodes.Parse(subscription.Modules);
        var disabled = ModuleCodes.Optional.Where(m => !licensed.Contains(m) || switchedOff.Contains(m)).ToHashSet();
        var state = new State(licensed, disabled, status == TenantStatus.Suspended || subscription?.Status == SubscriptionStatus.Suspended);
        cache.Set(key, state, TimeSpan.FromSeconds(30));
        return state;
    }

    internal static HashSet<string> Parse(string raw) => ModuleCodes.Parse(raw);

    /// <summary>After a licence, status or company switch changes: every company of that tenant reloads.</summary>
    internal static void InvalidateTenant(IMemoryCache cache, Guid tenantId) => cache.Set($"modules-ver:{tenantId}", Guid.NewGuid());
}

internal static class PlatformEndpoints
{
    internal static readonly (string Code, string En, string Ar)[] Names =
    [
        (ModuleCodes.Hr, "HR & Payroll", "الموارد البشرية والرواتب"),
        (ModuleCodes.Ess, "Employee self-service", "الخدمة الذاتية للموظفين"),
        (ModuleCodes.Inventory, "Inventory", "المخزون"),
        (ModuleCodes.Purchasing, "Purchasing", "المشتريات"),
        (ModuleCodes.Sales, "Sales", "المبيعات"),
        (ModuleCodes.Pos, "Point of sale", "نقاط البيع"),
        (ModuleCodes.Crm, "CRM & commissions", "إدارة العملاء والعمولات"),
        (ModuleCodes.Projects, "Projects & contracting", "المشاريع والمقاولات"),
        (ModuleCodes.Subcontracts, "Subcontracts", "عقود الباطن"),
        (ModuleCodes.Assets, "Fixed assets", "الأصول الثابتة"),
        (ModuleCodes.Budgets, "Budgets", "الموازنات"),
        (ModuleCodes.Tailoring, "Tailoring & abaya orders", "طلبات الخياطة والعبايات"),
        (ModuleCodes.Salon, "Salon & appointments", "الصالون والمواعيد"),
        (ModuleCodes.Workshop, "Car workshop job cards", "بطاقات عمل ورشة السيارات"),
        (ModuleCodes.Ai, "AI assistant", "المساعد الذكي")
    ];

    private static IEnumerable<(string Code, string En, string Ar, string[] Requires)> Catalog =>
        Names.Select(n => (n.Code, n.En, n.Ar, ModuleCodes.Requires.GetValueOrDefault(n.Code) ?? []));

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
        var licensed = await access.LicensedAsync(ct);
        return Catalog.Select(m => new ModuleDto(m.Code, m.En, m.Ar, !disabled.Contains(m.Code), m.Requires, licensed.Contains(m.Code))).ToList();
    }

    private static async Task<List<ModuleDto>> SaveModules(ModuleSwitchRequest r, ErpDbContext db, ITenantContext t, IMemoryCache cache, IModuleAccess access,
        CancellationToken ct)
    {
        var licensed = await access.LicensedAsync(ct);
        // Modules outside the subscription stay off whatever the company asks for.
        var requested = (r.Disabled ?? []).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
        var disabled = requested.Concat(ModuleCodes.Optional.Where(m => !licensed.Contains(m))).Distinct().ToList();
        if (disabled.Any(d => !ModuleCodes.Optional.Contains(d)))
            throw new BusinessRuleException("core.unknown_module", "Unknown module.", "وحدة غير معروفة.");
        // Switching a module off also switches off the modules that depend on it (e.g. sales → CRM, projects, tailoring, salon).
        bool changed;
        do
        {
            changed = false;
            foreach (var m in Catalog.Where(m => !disabled.Contains(m.Code) && m.Requires.Any(disabled.Contains)))
            {
                disabled.Add(m.Code);
                if (licensed.Contains(m.Code)) requested.Add(m.Code);
                changed = true;
            }
        } while (changed);
        var company = await db.Set<Company>().SingleAsync(c => c.Id == t.RequireCompanyId(), ct);
        // Only the company's own choices are stored; licensing is applied on top, so an upgrade takes effect without re-saving.
        company.DisabledModules = string.Join(',', requested.Where(licensed.Contains).Order());
        await db.SaveChangesAsync(ct);
        ModuleAccess.InvalidateTenant(cache, t.RequireTenantId());
        var set = disabled.ToHashSet();
        return Catalog.Select(m => new ModuleDto(m.Code, m.En, m.Ar, !set.Contains(m.Code), m.Requires, licensed.Contains(m.Code))).ToList();
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

namespace Erp.SharedKernel.Application;

/// <summary>Who is calling, resolved from the authenticated token (never from request data).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? CompanyId { get; }
    Guid? UserId { get; }

    Guid RequireTenantId();
    Guid RequireCompanyId();

    /// <summary>The acting user, or <see cref="SystemUser.Id"/> for background work.</summary>
    Guid ActingUserId { get; }

    /// <summary>Switch context. Only used by authentication, provisioning and background jobs.</summary>
    void Set(Guid tenantId, Guid? companyId, Guid? userId);
}

public static class SystemUser
{
    public static readonly Guid Id = Guid.Empty;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Today's date in Qatar (Asia/Qatar, UTC+3, no daylight saving).</summary>
    DateOnly Today { get; }
}

/// <summary>Gapless document numbers. Must be called inside the transaction that saves the document.</summary>
public interface INumberingService
{
    Task<string> NextAsync(string docType, string defaultPrefix, DateOnly documentDate, Guid? branchId = null,
        CancellationToken ct = default);
}

/// <summary>Messages for external side effects, written in the same transaction as the business change.</summary>
public interface IOutbox
{
    void Enqueue(string type, object payload);
}

public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasAsync(string permission, CancellationToken ct = default);
}

public sealed record PermissionDefinition(string Code, string DescriptionEn, string DescriptionAr, params string[] DefaultRoles);

/// <summary>Each module declares its permissions; Core seeds them into default roles at provisioning.</summary>
public interface IPermissionProvider
{
    IEnumerable<PermissionDefinition> GetPermissions();
}

public sealed record TenantProvisioningContext(
    Guid TenantId,
    Guid CompanyId,
    Guid BranchId,
    Guid AdminUserId,
    int FiscalYearStartMonth,
    DateOnly Today);

/// <summary>Seeds a module's defaults for a new tenant. Runs inside the provisioning transaction.</summary>
public interface ITenantProvisioner
{
    int Order { get; }
    Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record TenantCompany(Guid TenantId, Guid CompanyId);

/// <summary>Enumerates active tenants and their companies for background jobs that work tenant by tenant.</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantCompany>> GetActiveCompaniesAsync(CancellationToken ct = default);
}

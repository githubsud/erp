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

public sealed record CompanyProfile(string NameEn, string NameAr, string? CrNo, string? EstablishmentId, string? AddressEn,
    string? AddressAr, string? Phone, string? Email, string? Tin = null);

/// <summary>Letterhead details of the current company, for printed documents in any module.</summary>
public interface ICompanyProfileProvider
{
    Task<CompanyProfile> GetAsync(CancellationToken ct = default);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string TextBody,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>Sends email. Called from outbox handlers only, so delivery is retried and never blocks a business transaction.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed record PortalAccount(Guid UserId, string Username, string TemporaryPassword);

/// <summary>Creates sign-ins for people who are not staff users (employees using self-service) without referencing Core.</summary>
public interface IUserAccounts
{
    /// <summary>Creates the user (or resets an existing one) with a temporary password that must be changed at first sign-in.</summary>
    Task<PortalAccount> CreateOrResetAsync(Guid? existingUserId, string username, string? email, string nameEn, string nameAr, string language,
        string roleCode, CancellationToken ct = default);

    Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);
}

/// <summary>Finds the people to notify for the current tenant.</summary>
public interface IUserDirectory
{
    /// <summary>Email addresses of active users holding <paramref name="permission"/> (owners included through the wildcard).</summary>
    Task<IReadOnlyList<string>> EmailsWithPermissionAsync(string permission, CancellationToken ct = default);

    /// <summary>Display names (English) of users of the current tenant, for reports that show who did something.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
}

public sealed record TenantCompany(Guid TenantId, Guid CompanyId);

/// <summary>Enumerates active tenants and their companies for background jobs that work tenant by tenant.</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantCompany>> GetActiveCompaniesAsync(CancellationToken ct = default);
}

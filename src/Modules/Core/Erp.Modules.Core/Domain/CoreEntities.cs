using Erp.SharedKernel.Domain;

namespace Erp.Modules.Core.Domain;

public enum TenantStatus { Active, Suspended, Closed }

/// <summary>A subscribing customer. Platform-level row (not tenant-owned): looked up by code at login.</summary>
public sealed class Tenant : Entity
{
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string BaseCurrency { get; init; } = Money.BaseCurrency;
    public string DefaultLocale { get; set; } = "ar-QA";
    public string TimeZone { get; init; } = "Asia/Qatar";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Company : TenantEntity
{
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }

    /// <summary>Commercial Registration number (MoCI).</summary>
    public string? CrNo { get; set; }

    /// <summary>Establishment (computer card) ID issued by the Ministry of Interior; used as Employer EID in WPS.</summary>
    public string? EstablishmentId { get; set; }

    /// <summary>Tax identification number (General Tax Authority / Dhareeba).</summary>
    public string? Tin { get; set; }

    /// <summary>Share of non-Qatari/non-GCC ownership; drives the taxable share for corporate income tax.</summary>
    public decimal ForeignOwnershipPct { get; set; }

    public string? AddressEn { get; set; }
    public string? AddressAr { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string BaseCurrency { get; init; } = Money.BaseCurrency;
    public uint Version { get; private set; }
}

public sealed class Branch : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AppUser : TenantEntity
{
    /// <summary>Staff sign in with their email; employees using self-service may have only a <see cref="Username"/>.</summary>
    public string? Email { get; init; }
    public string? Username { get; init; }
    public required string DisplayNameEn { get; set; }
    public required string DisplayNameAr { get; set; }

    [Sensitive]
    public string PasswordHash { get; set; } = "";

    public string PreferredLanguage { get; set; } = "ar";
    public bool IsActive { get; set; } = true;

    /// <summary>Set for temporary passwords handed out by HR; cleared when the user chooses their own.</summary>
    public bool MustChangePassword { get; set; }
    public Guid? DefaultCompanyId { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool MfaEnabled { get; set; }
    [Sensitive] public string? MfaSecret { get; set; }
    [Sensitive] public string? MfaPendingSecret { get; set; }
    public long MfaLastStep { get; set; }
    public uint Version { get; private set; }
}

/// <summary>Only a hash of the token is stored; the raw value lives in the client session.</summary>
public sealed class RefreshToken : TenantEntity, INotAudited
{
    public Guid UserId { get; init; }
    public required string TokenHash { get; init; }
    public Guid? CompanyId { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedBy { get; set; }
}

public sealed class Role : TenantEntity
{
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsSystem { get; init; }
    public List<RolePermission> Permissions { get; init; } = [];
}

public sealed class RolePermission : TenantEntity
{
    public Guid RoleId { get; init; }
    public required string Permission { get; init; }
}

public sealed class UserRole : TenantEntity
{
    public Guid UserId { get; init; }
    public Guid RoleId { get; init; }
}

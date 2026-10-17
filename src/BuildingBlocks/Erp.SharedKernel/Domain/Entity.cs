namespace Erp.SharedKernel.Domain;

/// <summary>Base for every persisted object. Ids are UUID v7 (time-ordered, index friendly).</summary>
public abstract class Entity
{
    public Guid Id { get; protected init; } = Guid.CreateVersion7();
}

/// <summary>Row belongs to exactly one tenant. Set by the persistence layer, never from user input.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public interface IAuditable
{
    Guid CreatedBy { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    Guid? UpdatedBy { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Rows may be inserted but never updated or deleted (ledger rows). Corrections are reversals.</summary>
public interface IImmutable;

/// <summary>Excluded from the generic change audit (high-volume or infrastructure rows).</summary>
public interface INotAudited;

/// <summary>Marks a property whose value must never appear in audit logs or default API output.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute;

public abstract class TenantEntity : Entity, ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

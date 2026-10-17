using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Infrastructure.Persistence;

public sealed class AuditLogEntry : TenantEntity, INotAudited
{
    public required string EntityType { get; init; }
    public Guid EntityId { get; init; }
    public required string Action { get; init; }
    public required string Changes { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.ToTable("audit_log", "core");
        b.Property(x => x.Changes).HasColumnType("jsonb");
    }
}

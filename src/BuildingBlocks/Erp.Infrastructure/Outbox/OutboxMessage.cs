using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Infrastructure.Outbox;

public sealed class OutboxMessage : TenantEntity, INotAudited
{
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset AvailableAt { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public DateTimeOffset? DeadLetteredAt { get; init; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_message", "core");
        b.Property(x => x.Payload).HasColumnType("jsonb");
    }
}

/// <summary>Adds messages to the current unit of work; they commit (or roll back) with the business change.</summary>
public sealed class EfOutbox(ErpDbContext db, IClock clock) : IOutbox
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue(string type, object payload) =>
        db.Add(new OutboxMessage
        {
            Type = type,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            AvailableAt = clock.UtcNow
        });
}

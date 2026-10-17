using System.Reflection;
using System.Text.Json;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Erp.Infrastructure.Persistence;

/// <summary>
/// Enforces tenant ownership and immutability on every save, stamps audit columns and writes a
/// change-level audit trail (old/new values) in the same transaction as the change itself.
/// </summary>
public sealed class AuditingInterceptor(ITenantContext tenant, IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();

        var now = clock.UtcNow;
        var actor = tenant.ActingUserId;
        var auditEntries = new List<AuditLogEntry>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged) continue;

            if (entry.Entity is IImmutable && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException(
                    $"{entry.Entity.GetType().Name} rows are immutable. Post a reversal instead of editing or deleting.");

            if (entry.Entity is ITenantOwned owned)
                EnforceTenant(entry, owned);

            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAt = now;
                    auditable.CreatedBy = actor;
                }
                else if (entry.State == EntityState.Modified)
                {
                    auditable.UpdatedAt = now;
                    auditable.UpdatedBy = actor;
                    entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedBy)).IsModified = false;
                }
            }

            if (entry.Entity is ITenantOwned and not (INotAudited or IImmutable or AuditLogEntry))
                auditEntries.Add(CreateAuditEntry(entry, now, actor));
        }

        if (auditEntries.Count > 0)
            context.AddRange(auditEntries);
    }

    private void EnforceTenant(EntityEntry entry, ITenantOwned owned)
    {
        var current = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot save tenant-owned data without a tenant context.");

        if (entry.State == EntityState.Added && owned.TenantId == Guid.Empty)
        {
            owned.TenantId = current;
            return;
        }

        if (owned.TenantId != current)
            throw new InvalidOperationException(
                $"Tenant mismatch on {entry.Entity.GetType().Name}: refusing to save another tenant's data.");

        if (entry.State == EntityState.Modified)
            entry.Property(nameof(ITenantOwned.TenantId)).IsModified = false;
    }

    private static AuditLogEntry CreateAuditEntry(EntityEntry entry, DateTimeOffset now, Guid actor)
    {
        var changes = new Dictionary<string, object?>();
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (name is nameof(IAuditable.CreatedAt) or nameof(IAuditable.CreatedBy) or nameof(IAuditable.UpdatedAt)
                or nameof(IAuditable.UpdatedBy) or nameof(ITenantOwned.TenantId)) continue;

            var sensitive = property.Metadata.PropertyInfo?.GetCustomAttribute<SensitiveAttribute>() is not null;
            object? Mask(object? v) => sensitive && v is not null ? "***" : v;

            switch (entry.State)
            {
                case EntityState.Added:
                    changes[name] = Mask(property.CurrentValue);
                    break;
                case EntityState.Deleted:
                    changes[name] = Mask(property.OriginalValue);
                    break;
                case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                    changes[name] = new { old = Mask(property.OriginalValue), @new = Mask(property.CurrentValue) };
                    break;
            }
        }

        return new AuditLogEntry
        {
            TenantId = ((ITenantOwned)entry.Entity).TenantId,
            EntityType = entry.Metadata.GetTableName() ?? entry.Entity.GetType().Name,
            EntityId = entry.Entity is Entity e ? e.Id : Guid.Empty,
            Action = entry.State switch
            {
                EntityState.Added => "create",
                EntityState.Deleted => "delete",
                _ => "update"
            },
            Changes = JsonSerializer.Serialize(changes),
            UserId = actor,
            OccurredAt = now,
            CreatedAt = now,
            CreatedBy = actor
        };
    }
}

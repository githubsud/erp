using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Erp.Infrastructure.Outbox;

/// <summary>Delivers one message type. Must be idempotent: delivery is at-least-once, keyed by <paramref name="messageId"/>.</summary>
public interface IOutboxHandler
{
    string MessageType { get; }
    Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct);
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 20;
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = 8;
}

/// <summary>
/// Polls <c>core.outbox_message</c> as the <c>erp_worker</c> role (the only role whose RLS policy spans tenants),
/// claims a batch with FOR UPDATE SKIP LOCKED (safe with several app instances), and runs each handler in a
/// DI scope bound to the message's tenant. Failures back off exponentially and dead-letter after MaxAttempts.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    DatabaseOptions database,
    OutboxOptions options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await DispatchBatchAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch loop failed; retrying shortly.");
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.WorkerConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var batch = new List<(Guid Id, Guid TenantId, string Type, string Payload, int Attempts)>();
        await using (var select = new NpgsqlCommand("""
            SELECT id, tenant_id, type, payload::text, attempts
              FROM core.outbox_message
             WHERE sent_at IS NULL AND dead_lettered_at IS NULL AND available_at <= now()
             ORDER BY created_at
             LIMIT @limit
               FOR UPDATE SKIP LOCKED
            """, connection, tx))
        {
            select.Parameters.AddWithValue("limit", options.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                batch.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }

        foreach (var message in batch)
        {
            try
            {
                await HandleAsync(message.Id, message.TenantId, message.Type, message.Payload, ct);
                await ExecuteAsync(connection, tx, "UPDATE core.outbox_message SET sent_at = now(), attempts = attempts + 1, updated_at = now() WHERE id = @id",
                    ("id", message.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var attempts = message.Attempts + 1;
                var deadLetter = attempts >= options.MaxAttempts;
                var delay = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts), 360));
                logger.LogWarning(ex, "Outbox message {Id} ({Type}) failed on attempt {Attempt}", message.Id, message.Type, attempts);
                await ExecuteAsync(connection, tx, """
                    UPDATE core.outbox_message
                       SET attempts = @attempts, last_error = @error, updated_at = now(),
                           available_at = now() + @delay,
                           dead_lettered_at = CASE WHEN @dead THEN now() END
                     WHERE id = @id
                    """,
                    ("id", message.Id), ("attempts", attempts), ("error", ex.Message), ("delay", delay), ("dead", deadLetter));
            }
        }

        await tx.CommitAsync(ct);
        return batch.Count;
    }

    private async Task HandleAsync(Guid id, Guid tenantId, string type, string payload, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, null);

        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.MessageType == type).ToList();
        if (handlers.Count == 0)
        {
            logger.LogDebug("No handler for outbox message type {Type}; marking as sent.", type);
            return;
        }

        using var document = JsonDocument.Parse(payload);
        foreach (var handler in handlers)
            await handler.HandleAsync(id, document.RootElement, ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}

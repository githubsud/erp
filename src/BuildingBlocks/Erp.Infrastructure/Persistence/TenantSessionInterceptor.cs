using System.Data.Common;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Erp.Infrastructure.Persistence;

/// <summary>
/// Second line of tenant isolation: every connection opened by EF carries the caller's tenant in the
/// <c>app.tenant_id</c> session setting, which PostgreSQL row-level security policies compare against.
/// Npgsql resets session state when a pooled connection is returned, and we set it again on every open,
/// so a tenant can never leak between requests.
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection, tenant);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, tenant);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static DbCommand CreateCommand(DbConnection connection, ITenantContext tenant)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant, false), set_config('app.user_id', @user, false)";
        AddParameter(command, "tenant", tenant.TenantId?.ToString() ?? string.Empty);
        AddParameter(command, "user", tenant.UserId?.ToString() ?? string.Empty);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

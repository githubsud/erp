using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Erp.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Owner/DDL connection used only by the migrator.</summary>
    public string OwnerConnectionString { get; set; } = "";

    /// <summary>Runtime connection (role <c>erp_app</c>: not owner, no BYPASSRLS).</summary>
    public string AppConnectionString { get; set; } = "";

    /// <summary>Outbox dispatcher connection (role <c>erp_worker</c>).</summary>
    public string WorkerConnectionString { get; set; } = "";

    public string AppRolePassword { get; set; } = "";
    public string WorkerRolePassword { get; set; } = "";
    public bool MigrateOnStartup { get; set; }
}

/// <summary>
/// Applies versioned SQL scripts (<c>V0001__name.sql</c>) once, in global version order, then re-runs
/// repeatable scripts (<c>R__name.sql</c>, e.g. grants) every time. Scripts are embedded resources in each
/// module's <c>Migrations</c> folder. Applied scripts are checksummed; editing one is an error.
/// </summary>
public sealed partial class DatabaseMigrator(DatabaseOptions options, IEnumerable<Assembly> assemblies, ILogger<DatabaseMigrator> logger)
{
    public const string AppRole = "erp_app";
    public const string WorkerRole = "erp_worker";
    private const long AdvisoryLockKey = 72_000_727;

    [GeneratedRegex(@"^V(?<version>\d+)__(?<name>[\w\-]+)\.sql$")]
    private static partial Regex VersionedPattern();

    [GeneratedRegex(@"^R__(?<name>[\w\-]+)\.sql$")]
    private static partial Regex RepeatablePattern();

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var (versioned, repeatable) = LoadScripts();

        await using var connection = new NpgsqlConnection(options.OwnerConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await EnsureRolesAsync(connection, ct);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS public.schema_migrations (
                    version     int PRIMARY KEY,
                    name        text NOT NULL,
                    checksum    text NOT NULL,
                    applied_at  timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            var applied = new Dictionary<int, string>();
            await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM public.schema_migrations", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) applied[reader.GetInt32(0)] = reader.GetString(1);

            // A backup from a newer version restored under an older program: its tables do not match this code.
            var unknown = applied.Keys.Except(versioned.Select(s => s.Version)).Order().ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"The database was made by a newer version of the ERP (migration V{unknown[^1]:D4} is unknown here). Install the newer version.");

            foreach (var script in versioned)
            {
                if (applied.TryGetValue(script.Version, out var checksum))
                {
                    if (checksum != script.Checksum)
                        throw new InvalidOperationException(
                            $"Migration V{script.Version:D4}__{script.Name} was changed after being applied. Add a new migration instead.");
                    continue;
                }

                logger.LogInformation("Applying migration V{Version:D4}__{Name}", script.Version, script.Name);
                await using var tx = await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, script.Sql, ct);
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO public.schema_migrations (version, name, checksum) VALUES (@v, @n, @c)", connection))
                {
                    insert.Parameters.AddWithValue("v", script.Version);
                    insert.Parameters.AddWithValue("n", script.Name);
                    insert.Parameters.AddWithValue("c", script.Checksum);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }

            foreach (var script in repeatable)
            {
                logger.LogDebug("Running repeatable script R__{Name}", script.Name);
                await ExecuteAsync(connection, script.Sql, ct);
            }
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private async Task EnsureRolesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        foreach (var (role, password) in new[] { (AppRole, options.AppRolePassword), (WorkerRole, options.WorkerRolePassword) })
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException($"A password for database role '{role}' must be configured.");

            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @r", connection);
            exists.Parameters.AddWithValue("r", role);
            if (await exists.ExecuteScalarAsync(ct) is not null) continue;

            // Runtime roles must never own tables or bypass row-level security.
            var literal = password.Replace("'", "''");
            await ExecuteAsync(connection,
                $"CREATE ROLE {role} LOGIN PASSWORD '{literal}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS", ct);
        }
    }

    private (List<Script> Versioned, List<Script> Repeatable) LoadScripts()
    {
        var versioned = new List<Script>();
        var repeatable = new List<Script>();

        foreach (var assembly in assemblies.Distinct())
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.Contains(".Migrations.") && r.EndsWith(".sql")))
        {
            var fileName = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            var sql = streamReader.ReadToEnd();
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

            if (VersionedPattern().Match(fileName) is { Success: true } v)
                versioned.Add(new Script(int.Parse(v.Groups["version"].Value), v.Groups["name"].Value, sql, checksum));
            else if (RepeatablePattern().Match(fileName) is { Success: true } r)
                repeatable.Add(new Script(0, r.Groups["name"].Value, sql, checksum));
            else
                throw new InvalidOperationException($"Migration file '{fileName}' does not follow V0001__name.sql or R__name.sql.");
        }

        var duplicate = versioned.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.Name))}");

        return (versioned.OrderBy(s => s.Version).ToList(), repeatable.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record Script(int Version, string Name, string Sql, string Checksum);
}

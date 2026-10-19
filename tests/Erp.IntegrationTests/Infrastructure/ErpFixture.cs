using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests.Infrastructure;

/// <summary>
/// One real PostgreSQL 17 container and one API host per test run. Every test provisions its own tenant(s),
/// which is itself a check that tenants are isolated from each other.
/// </summary>
public sealed class ErpFixture : IAsyncLifetime
{
    public const string PlatformKey = "test-platform-key";
    // Database roles are cluster-wide; these match the local development server's roles.
    public const string AppPassword = "erp_app_dev_password";
    public const string WorkerPassword = "erp_worker_dev_password";

    /// <summary>Owner connection to an existing server. Each run creates and drops its own database there.</summary>
    public const string ServerEnvironmentVariable = "ERP_TEST_POSTGRES";
    private const string DefaultServer = "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private PostgreSqlContainer? _container;
    private string? _serverConnectionString;
    private string? _databaseName;
    private WebApplicationFactory<Program>? _factory;

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Fixture not started.");
    public string OwnerConnectionString { get; private set; } = "";

    public string AppConnectionString =>
        new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "erp_app", Password = AppPassword }.ToString();

    public string WorkerConnectionString =>
        new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "erp_worker", Password = WorkerPassword }.ToString();

    public async Task InitializeAsync()
    {
        OwnerConnectionString = await CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:OwnerConnectionString", OwnerConnectionString);
            builder.UseSetting("Database:AppConnectionString", AppConnectionString);
            builder.UseSetting("Database:WorkerConnectionString", WorkerConnectionString);
            builder.UseSetting("Database:AppRolePassword", AppPassword);
            builder.UseSetting("Database:WorkerRolePassword", WorkerPassword);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("Outbox:Enabled", "false");
            builder.UseSetting("Jobs:DocumentExpiry:Enabled", "false");
            builder.UseSetting("Jobs:ReportSchedules:Enabled", "false");
            builder.UseSetting("Jobs:Billing:Enabled", "false");
            builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
            builder.UseSetting("Security:FieldEncryptionKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
            builder.UseSetting("Security:BlindIndexKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 101)).ToArray()));
            builder.UseSetting("Platform:ApiKey", PlatformKey);
        });
        _ = _factory.Server; // starts the host and runs migrations
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
        else if (_serverConnectionString is not null && _databaseName is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_serverConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    /// <summary>Uses an existing PostgreSQL server when reachable (fast, no Docker API needed); otherwise starts a container.</summary>
    private async Task<string> CreateDatabaseAsync()
    {
        var server = Environment.GetEnvironmentVariable(ServerEnvironmentVariable) ?? DefaultServer;
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Timeout = 3 }.ToString());
            await connection.OpenAsync();
            _databaseName = "erp_test_" + Guid.NewGuid().ToString("N")[..10];
            await new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection).ExecuteNonQueryAsync();
            _serverConnectionString = server;
            return new NpgsqlConnectionStringBuilder(server) { Database = _databaseName }.ToString();
        }
        catch (NpgsqlException) when (Environment.GetEnvironmentVariable(ServerEnvironmentVariable) is null)
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("erp").WithUsername("postgres").WithPassword("postgres").Build();
            await _container.StartAsync();
            return _container.GetConnectionString();
        }
    }

    /// <summary>Provisions a fresh tenant and returns a client logged in as its owner.</summary>
    public async Task<TenantSession> CreateTenantAsync(string? code = null)
    {
        code ??= "t-" + Guid.NewGuid().ToString("N")[..12];
        var password = "Owner-Password-123";
        var email = $"owner@{code}.qa";

        var platform = Factory.CreateClient();
        platform.DefaultRequestHeaders.Add("X-Platform-Key", PlatformKey);
        var provision = await platform.PostAsJsonAsync("/api/platform/tenants", new
        {
            code,
            nameEn = $"Test Company {code}",
            nameAr = "شركة الاختبار",
            establishmentId = "12345678",
            adminEmail = email,
            adminPassword = password,
            adminNameEn = "Owner",
            preferredLanguage = "en"
        }, Json);
        await provision.EnsureSuccessAsync();
        var tenant = (await provision.Content.ReadFromJsonAsync<ProvisionedTenant>(Json))!;

        var client = await LoginAsync(code, email, password);
        return new TenantSession(tenant.TenantId, tenant.CompanyId, tenant.BranchId, tenant.AdminUserId, code, client);
    }

    public async Task<HttpClient> LoginAsync(string code, string email, string password)
    {
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { tenantCode = code, email, password }, Json);
        await login.EnsureSuccessAsync();
        var body = (await login.Content.ReadFromJsonAsync<LoginBody>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return client;
    }

    /// <summary>Opens a raw connection as the runtime role with a tenant set, bypassing the application layer.</summary>
    public async Task<NpgsqlConnection> OpenAppConnectionAsync(Guid? tenantId)
    {
        var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection);
        cmd.Parameters.AddWithValue("t", tenantId?.ToString() ?? "");
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    private sealed record ProvisionedTenant(Guid TenantId, string Code, Guid CompanyId, Guid BranchId, Guid AdminUserId);

    private sealed record LoginBody(string AccessToken);
}

public sealed record TenantSession(Guid TenantId, Guid CompanyId, Guid BranchId, Guid AdminUserId, string Code, HttpClient Client);

[CollectionDefinition(Name)]
public sealed class ErpCollection : ICollectionFixture<ErpFixture>
{
    public const string Name = "erp";
}

public static class HttpExtensions
{
    public static async Task EnsureSuccessAsync(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        await response.EnsureSuccessAsync();
        return (await response.Content.ReadFromJsonAsync<T>(ErpFixture.Json))!;
    }

    public static async Task<T> GetAsync<T>(this HttpClient client, string url) => await (await client.GetAsync(url)).ReadAsync<T>();

    public static async Task<T> PostAsync<T>(this HttpClient client, string url, object body) =>
        await (await client.PostAsJsonAsync(url, body, ErpFixture.Json)).ReadAsync<T>();

    public static async Task<T> PutAsync<T>(this HttpClient client, string url, object body) =>
        await (await client.PutAsJsonAsync(url, body, ErpFixture.Json)).ReadAsync<T>();

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, ErpFixture.Json);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body) =>
        client.PutAsJsonAsync(url, body, ErpFixture.Json);

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(ErpFixture.Json);
}

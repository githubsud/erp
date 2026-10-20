using Erp.IntegrationTests.Infrastructure;
using Npgsql;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class PlatformTests(ErpFixture fixture)
{
    private static readonly string[] TenantSchemas = ["core", "fin", "hr", "inv", "pur", "sal", "ai"];
    private static readonly string[] PlatformTables =
        ["core.tenant", "core.platform_admin", "core.plan", "core.subscription", "core.subscription_invoice", "core.subscription_payment",
         "ai.provider", "ai.usage_day"];

    [Fact]
    public async Task Every_tenant_table_follows_the_schema_conventions()
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT n.nspname || '.' || c.relname AS table_name,
                   c.relrowsecurity, c.relforcerowsecurity,
                   bool_or(a.attname = 'tenant_id')  AS has_tenant,
                   bool_or(a.attname = 'created_by') AS has_created_by,
                   bool_or(a.attname = 'created_at') AS has_created_at,
                   bool_or(a.attname = 'updated_at') AS has_updated_at,
                   bool_or(format_type(a.atttypid, a.atttypmod) IN ('real', 'double precision')) AS has_float
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
             WHERE c.relkind = 'r' AND n.nspname = ANY(@schemas)
             GROUP BY n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity
            """, connection);
        cmd.Parameters.AddWithValue("schemas", TenantSchemas);

        var violations = new List<string>();
        var count = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            count++;
            var table = reader.GetString(0);
            if (reader.GetBoolean(7)) violations.Add($"{table}: uses floating point (use numeric)");
            if (PlatformTables.Contains(table)) continue;
            if (!reader.GetBoolean(3)) violations.Add($"{table}: missing tenant_id");
            if (!reader.GetBoolean(1) || !reader.GetBoolean(2)) violations.Add($"{table}: row-level security not enabled and forced");
            if (!reader.GetBoolean(4) || !reader.GetBoolean(5) || !reader.GetBoolean(6)) violations.Add($"{table}: missing audit columns");
        }

        count.ShouldBeGreaterThan(10);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Runtime_role_cannot_bypass_row_level_security()
    {
        await using var connection = await fixture.OpenAppConnectionAsync(null);
        await using var cmd = new NpgsqlCommand("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeFalse();
        reader.GetBoolean(1).ShouldBeFalse();
    }

    [Fact]
    public async Task Database_returns_only_the_session_tenants_rows()
    {
        var a = await fixture.CreateTenantAsync();
        var b = await fixture.CreateTenantAsync();

        await using (var none = await fixture.OpenAppConnectionAsync(null))
            (await CountAsync(none, "SELECT count(*) FROM fin.account")).ShouldBe(0);

        await using var asA = await fixture.OpenAppConnectionAsync(a.TenantId);
        (await CountAsync(asA, $"SELECT count(*) FROM fin.account WHERE tenant_id = '{b.TenantId}'")).ShouldBe(0);
        (await CountAsync(asA, "SELECT count(*) FROM fin.account")).ShouldBeGreaterThan(0);
        (await CountAsync(asA, "SELECT count(DISTINCT tenant_id) FROM core.company")).ShouldBe(1);

        // Writing a row for another tenant is rejected by the policy's WITH CHECK.
        var insert = new NpgsqlCommand(
            $"INSERT INTO fin.cost_center (id, tenant_id, company_id, code, name_en, name_ar, created_by) VALUES (gen_random_uuid(), '{b.TenantId}', '{b.CompanyId}', 'X', 'x', 'x', gen_random_uuid())",
            asA);
        var error = await Should.ThrowAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Api_does_not_expose_another_tenants_records()
    {
        var a = await fixture.CreateTenantAsync();
        var b = await fixture.CreateTenantAsync();
        var voucher = await FinanceTests.PostSimpleVoucherAsync(a.Client, 1000m);

        (await b.Client.GetAsync($"/api/finance/journal-entries/{voucher.JournalEntryId}")).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);

        // Using tenant A's account ids from tenant B must fail: they do not exist for B.
        var aAccounts = await a.Client.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        var response = await b.Client.PostJsonAsync("/api/finance/journal-vouchers", new
        {
            date = FinanceTests.Today,
            memoEn = "cross-tenant attempt",
            lines = new object[]
            {
                new { accountId = aAccounts.Single(x => x.Code == "1120").Id, debit = 10m, credit = 0m },
                new { accountId = aAccounts.Single(x => x.Code == "3110").Id, debit = 0m, credit = 10m }
            }
        });
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Platform_endpoints_require_the_platform_key()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostJsonAsync("/api/platform/tenants", new { code = "nokey-co" });
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changes_are_written_to_the_audit_log()
    {
        var t = await fixture.CreateTenantAsync();
        var response = await t.Client.PostJsonAsync("/api/finance/cost-centers", new { code = "SITE-A", nameEn = "Site A", nameAr = "الموقع أ" });
        await response.EnsureSuccessAsync();

        await using var connection = await fixture.OpenAppConnectionAsync(t.TenantId);
        (await CountAsync(connection, "SELECT count(*) FROM core.audit_log WHERE entity_type = 'cost_center' AND action = 'create' AND changes->>'Code' = 'SITE-A'"))
            .ShouldBe(1);
        // Password hashes never reach the audit trail.
        (await CountAsync(connection, "SELECT count(*) FROM core.audit_log WHERE entity_type = 'app_user' AND changes->>'PasswordHash' <> '***'"))
            .ShouldBe(0);
    }

    internal static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}

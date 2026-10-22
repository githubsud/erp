using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;
using Erp.Modules.HR.Documents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests;

/// <summary>Fixes from the security review: who may hand out access, how fast access ends, personal numbers, sign-in guessing.</summary>
[Collection(ErpCollection.Name)]
public sealed class SecurityTests(ErpFixture fixture)
{
    private const string Password = "User-Password-123";

    private sealed record Session(string AccessToken, string RefreshToken);

    private static async Task<Guid> AddUserAsync(HttpClient owner, string email, params string[] roles) =>
        (await owner.PostAsync<JsonElement>("/api/core/users",
            new { email, displayNameEn = email, password = Password, preferredLanguage = "en", roles })).GetProperty("id").GetGuid();

    private async Task<Session> SignInAsync(string code, string email, string password = Password)
    {
        var login = await fixture.Factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { tenantCode = code, email, password }, ErpFixture.Json);
        return await login.ReadAsync<Session>();
    }

    private HttpClient ClientFor(Session s)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", s.AccessToken);
        return client;
    }

    [Fact]
    public async Task A_manager_cannot_make_anyone_an_owner_nor_demote_or_switch_off_the_owner()
    {
        var t = await fixture.CreateTenantAsync();
        await AddUserAsync(t.Client, $"gm@{t.Code}.qa", "general_manager");
        var gm = await fixture.LoginAsync(t.Code, $"gm@{t.Code}.qa", Password);

        // Within the manager's own access: allowed.
        var cashier = await AddUserAsync(gm, $"cashier@{t.Code}.qa", "salesperson");

        var owner = await gm.PostJsonAsync("/api/core/users",
            new { email = $"evil@{t.Code}.qa", displayNameEn = "Evil", password = Password, preferredLanguage = "en", roles = new[] { "owner" } });
        (await owner.ProblemAsync()).GetProperty("code").GetString().ShouldBe("user.beyond_own_access");
        // Changing roles is for owners only.
        (await gm.PutJsonAsync($"/api/core/users/{cashier}/roles", new { roles = new[] { "owner" } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await gm.PutJsonAsync($"/api/core/users/{t.AdminUserId}/roles", new { roles = new[] { "salesperson" } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await gm.PostJsonAsync($"/api/core/users/{t.AdminUserId}/deactivate", new { })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("user.beyond_own_access");

        // The owner may do all of it.
        await (await t.Client.PutJsonAsync($"/api/core/users/{cashier}/roles", new { roles = new[] { "accountant" } })).EnsureSuccessAsync();
    }

    [Fact]
    public async Task A_deactivated_user_is_cut_off_at_once_and_a_new_password_signs_out_other_devices()
    {
        var t = await fixture.CreateTenantAsync();
        var email = $"clerk@{t.Code}.qa";
        var id = await AddUserAsync(t.Client, email, "accountant");
        var session = await SignInAsync(t.Code, email);
        var clerk = ClientFor(session);
        (await clerk.GetAsync("/api/finance/accounts")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await (await t.Client.PostJsonAsync($"/api/core/users/{id}/deactivate", new { })).EnsureSuccessAsync();
        // The access token has not expired, but it no longer opens anything, and it cannot be renewed.
        (await clerk.GetAsync("/api/finance/accounts")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.Factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { session.RefreshToken }, ErpFixture.Json))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Two devices; the password is changed on the phone: the laptop's session ends, the phone's goes on.
        var other = $"other@{t.Code}.qa";
        await AddUserAsync(t.Client, other, "accountant");
        var laptop = await SignInAsync(t.Code, other);
        var phone = await SignInAsync(t.Code, other);
        await (await ClientFor(phone).PostJsonAsync("/api/auth/password",
            new { currentPassword = Password, newPassword = "New-Password-456", refreshToken = phone.RefreshToken })).EnsureSuccessAsync();
        var client = fixture.Factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/refresh", new { laptop.RefreshToken }, ErpFixture.Json)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/auth/refresh", new { phone.RefreshToken }, ErpFixture.Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resetting_a_portal_sign_in_never_hands_out_a_password_for_an_account_with_more_access()
    {
        var t = await fixture.CreateTenantAsync();
        var e = await PayrollTests.HireAsync(t.Client, "E100", "Sami Haddad", "JO", new DateOnly(2025, 1, 1), "28440012345", "000000000004444444444");
        var access = await t.Client.PostAsync<SelfServiceTests.Access>($"/api/hr/employees/{e.Id}/portal-access", new { });
        // Resetting a plain portal sign-in still works.
        await t.Client.PostAsync<SelfServiceTests.Access>($"/api/hr/employees/{e.Id}/portal-access", new { });

        // The owner makes the same person a cashier too: HR can no longer reset that sign-in.
        Guid userId;
        await using (var connection = await fixture.OpenAppConnectionAsync(t.TenantId))
        await using (var find = new NpgsqlCommand("SELECT id FROM core.app_user WHERE username = @u", connection))
        {
            find.Parameters.AddWithValue("u", access.Username);
            userId = (Guid)(await find.ExecuteScalarAsync())!;
        }
        await (await t.Client.PutJsonAsync($"/api/core/users/{userId}/roles", new { roles = new[] { "employee", "salesperson" } })).EnsureSuccessAsync();
        var reset = await t.Client.PostJsonAsync($"/api/hr/employees/{e.Id}/portal-access", new { });
        (await reset.ProblemAsync()).GetProperty("code").GetString().ShouldBe("auth.portal_is_staff");
    }

    [Fact]
    public async Task Document_numbers_are_encrypted_and_masked_for_people_without_sensitive_access()
    {
        var t = await fixture.CreateTenantAsync();
        var e = await PayrollTests.HireAsync(t.Client, "E200", "Maria Santos", "PH", new DateOnly(2025, 1, 1), "28460812345", "000000000005555555555");
        var doc = await t.Client.PostAsync<JsonElement>($"/api/hr/employees/{e.Id}/documents",
            new { docType = "Passport", docNo = "P7654321", expiryDate = FinanceTests.Today.AddDays(20) });
        doc.GetProperty("docNo").GetString().ShouldBe("P7654321");
        var docId = doc.GetProperty("id").GetGuid();

        // Stored encrypted.
        await using (var connection = await fixture.OpenAppConnectionAsync(t.TenantId))
        await using (var read = new NpgsqlCommand($"SELECT doc_no_encrypted FROM hr.employee_document WHERE id = '{docId}'", connection))
            ((string)(await read.ExecuteScalarAsync())!).ShouldStartWith("v1:");

        // An auditor sees employees but not personal numbers.
        await AddUserAsync(t.Client, $"auditor@{t.Code}.qa", "auditor");
        var auditor = await fixture.LoginAsync(t.Code, $"auditor@{t.Code}.qa", Password);
        (await auditor.GetAsync<List<JsonElement>>($"/api/hr/employees/{e.Id}/documents")).Single().GetProperty("docNo").GetString().ShouldBe("••••4321");
        (await auditor.GetAsync<List<JsonElement>>("/api/hr/documents/expiring")).Single(d => d.GetProperty("scope").GetString() == "employee")
            .GetProperty("docNo").GetString().ShouldBe("••••4321");

        // Saving the form with the masked number keeps the real one.
        await (await t.Client.PutJsonAsync($"/api/hr/employee-documents/{docId}",
            new { docType = "Passport", docNo = "••••4321", expiryDate = FinanceTests.Today.AddDays(400) })).EnsureSuccessAsync();
        (await t.Client.GetAsync<List<JsonElement>>($"/api/hr/employees/{e.Id}/documents")).Single().GetProperty("docNo").GetString().ShouldBe("P7654321");

        // A number saved before encryption is read as it is, then encrypted by the start-up step.
        await using (var connection = await fixture.OpenAppConnectionAsync(t.TenantId))
        await using (var plain = new NpgsqlCommand($"UPDATE hr.employee_document SET doc_no_encrypted = 'P1112223' WHERE id = '{docId}'", connection))
            await plain.ExecuteNonQueryAsync();
        (await t.Client.GetAsync<List<JsonElement>>($"/api/hr/employees/{e.Id}/documents")).Single().GetProperty("docNo").GetString().ShouldBe("P1112223");
        (await fixture.Factory.Services.GetRequiredService<DocumentExpiryJob>().EncryptOldNumbersAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
        await using (var connection = await fixture.OpenAppConnectionAsync(t.TenantId))
        await using (var read = new NpgsqlCommand($"SELECT doc_no_encrypted FROM hr.employee_document WHERE id = '{docId}'", connection))
            ((string)(await read.ExecuteScalarAsync())!).ShouldStartWith("v1:");
        (await t.Client.GetAsync<List<JsonElement>>($"/api/hr/employees/{e.Id}/documents")).Single().GetProperty("docNo").GetString().ShouldBe("P1112223");
    }

    [Fact]
    public async Task Sign_in_guessing_is_slowed_down_per_device()
    {
        await using var strict = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Security:SignInAttemptsPerMinute", "3"));
        var client = strict.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "100.64.0.7");
        for (var i = 0; i < 3; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { tenantCode = "nobody", email = "a@b.qa", password = "x" }, ErpFixture.Json))
                .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var blocked = await client.PostAsJsonAsync("/api/auth/login", new { tenantCode = "nobody", email = "a@b.qa", password = "x" }, ErpFixture.Json);
        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("auth.too_many_attempts");

        // Another phone behind the same proxy is not affected.
        var other = strict.CreateClient();
        other.DefaultRequestHeaders.Add("X-Forwarded-For", "100.64.0.8");
        (await other.PostAsJsonAsync("/api/auth/login", new { tenantCode = "nobody", email = "a@b.qa", password = "x" }, ErpFixture.Json))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Responses_carry_browser_protections_and_oversized_excel_files_are_refused()
    {
        var health = await fixture.Factory.CreateClient().GetAsync("/health");
        health.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        health.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");

        // A "spreadsheet" with thousands of parts inside is refused before it is unpacked.
        var t = await fixture.CreateTenantAsync();
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < 10_001; i++)
                zip.CreateEntry($"x/{i}.xml", CompressionLevel.NoCompression);
        var form = new MultipartFormDataContent { { new ByteArrayContent(zipped.ToArray()), "file", "items.xlsx" } };
        (await (await t.Client.PostAsync("/api/inventory/imports/items?dryRun=true", form)).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("import.too_large");
    }
}

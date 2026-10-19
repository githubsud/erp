using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class FieldStaffTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private async Task<HttpClient> EmployeeAsync(TenantSession t, string empNo)
    {
        var c = t.Client;
        await PayrollTests.ConfigureWpsAsync(c);
        var e = await PayrollTests.HireAsync(c, empNo, "Field Worker", "IN", new DateOnly(2024, 3, 1), "28435612399", "000000000001111111199");
        await PayrollTests.ContractAsync(c, e.Id, new DateOnly(2024, 3, 1), ("BASIC", 3000), ("HOUSING", 800));
        var access = await c.PostAsync<SelfServiceTests.Access>($"/api/hr/employees/{e.Id}/portal-access", new { });
        var client = fixture.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { tenantCode = t.Code, email = empNo, password = access.TemporaryPassword }, ErpFixture.Json);
        var body = await login.ReadAsync<SelfServiceTests.LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return client;
    }

    [Fact]
    public async Task Expense_claims_are_approved_into_the_next_open_payroll_with_their_receipt()
    {
        var t = await fixture.CreateTenantAsync();
        var me = await EmployeeAsync(t, "F001");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(Today.ToString("yyyy-MM-dd")), "expenseDate" },
            { new StringContent("Fuel"), "category" },
            { new StringContent("Site visit to Ras Laffan"), "description" },
            { new StringContent("85.50"), "amount" }
        };
        var photo = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        photo.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(photo, "receipt", "fuel.png");
        var claim = await (await me.PostAsync("/api/me/expenses", form)).ReadAsync<JsonElement>();
        var id = claim.GetProperty("id").GetGuid();
        (await me.GetAsync<List<JsonElement>>("/api/me/expenses")).Single().GetProperty("status").GetString().ShouldBe("Submitted");

        var c = t.Client;
        var pending = await c.GetAsync<List<JsonElement>>("/api/hr/expenses?status=Submitted");
        pending.Single().GetProperty("hasReceipt").GetBoolean().ShouldBeTrue();
        (await (await c.GetAsync($"/api/hr/expenses/{id}/receipt")).Content.ReadAsByteArrayAsync()).Length.ShouldBe(7);
        (await (await c.PostJsonAsync($"/api/hr/expenses/{id}/decide", new { approve = false })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("hr.expense_reason");
        var decided = await c.PostAsync<JsonElement>($"/api/hr/expenses/{id}/decide", new { approve = true });
        decided.GetProperty("payrollMonth").GetInt32().ShouldBe(Today.Month);

        var inputs = await c.GetAsync<List<JsonElement>>($"/api/hr/payroll-inputs?year={Today.Year}&month={Today.Month}");
        var input = inputs.Single(i => i.GetProperty("source").GetString() == "Expense");
        input.GetProperty("amount").GetDecimal().ShouldBe(85.5m);
        (await me.PostJsonAsync($"/api/me/expenses/{id}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Phone_punches_count_only_inside_a_work_site_and_build_the_attendance_day()
    {
        var t = await fixture.CreateTenantAsync();
        var me = await EmployeeAsync(t, "F002");
        (await (await me.PostJsonAsync("/api/me/punch", new { kind = "In", latitude = 25.2854m, longitude = 51.5310m })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("hr.no_sites");
        await t.Client.PostAsync<Guid>("/api/hr/work-sites", new
        {
            code = "WEST-BAY", nameEn = "West Bay site", nameAr = "موقع الخليج الغربي", latitude = 25.2854m, longitude = 51.5310m, radiusM = 200
        });

        // About 1.6 km north: refused (but logged).
        var far = await me.PostAsync<JsonElement>("/api/me/punch", new { kind = "In", latitude = 25.3000m, longitude = 51.5310m, accuracyM = 15 });
        far.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        far.GetProperty("distanceM").GetInt32().ShouldBeInRange(1_550, 1_700);
        (await (await me.PostJsonAsync("/api/me/punch", new { kind = "Out", latitude = 25.2855m, longitude = 51.5311m })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("hr.no_punch_in");

        var punchIn = await me.PostAsync<JsonElement>("/api/me/punch", new { kind = "In", latitude = 25.2855m, longitude = 51.5311m, accuracyM = 10 });
        punchIn.GetProperty("accepted").GetBoolean().ShouldBeTrue();
        punchIn.GetProperty("siteCode").GetString().ShouldBe("WEST-BAY");
        var punchOut = await me.PostAsync<JsonElement>("/api/me/punch", new { kind = "Out", latitude = 25.2853m, longitude = 51.5309m });
        punchOut.GetProperty("accepted").GetBoolean().ShouldBeTrue();

        var punches = await t.Client.GetAsync<List<JsonElement>>($"/api/hr/punches?from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}");
        punches.Count.ShouldBe(3);
        punches.Count(p => !p.GetProperty("accepted").GetBoolean()).ShouldBe(1);
        var qatarToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var month = await t.Client.GetAsync<List<JsonElement>>($"/api/hr/attendance?year={qatarToday.Year}&month={qatarToday.Month}");
        month.Single().GetProperty("days").GetInt32().ShouldBe(1);
    }
}

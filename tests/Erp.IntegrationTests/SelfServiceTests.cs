using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SelfServiceTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;
    private static readonly DateOnly MonthStart = new(Today.Year, Today.Month, 1);

    public sealed record Access(string Username, string TemporaryPassword);
    public sealed record LoginBody(string AccessToken, bool MustChangePassword, List<string> Permissions);
    public sealed record MyPayslip(Guid Id, Guid RunId, int Year, int Month, decimal Gross, decimal Net);
    public sealed record MyProfile(string EmpNo, string NameEn, string? Qid, string? Iban, decimal MonthlySalary, int? AnnualLeaveDays);
    public sealed record MyLeaveRequest(Guid Id, string LeaveTypeCode, string Status);
    public sealed record MyLeave(List<MyLeaveRequest> Requests);
    public sealed record MyDocument(string DocType, string Status, int? DaysLeft);

    private async Task<(HttpClient Client, LoginBody Body)> SignInAsync(string code, string login, string password)
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { tenantCode = code, email = login, password }, ErpFixture.Json);
        var body = await response.ReadAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return (client, body);
    }

    [Fact]
    public async Task Employee_signs_in_with_employee_number_and_sees_only_their_own_records()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await PayrollTests.ConfigureWpsAsync(c);
        var ramesh = await PayrollTests.HireAsync(c, "E001", "Ramesh Kumar", "IN", new DateOnly(2024, 3, 1), "28435612345", "000000000001111111111");
        var maria = await PayrollTests.HireAsync(c, "E002", "Maria Santos", "PH", new DateOnly(2024, 3, 1), "29160812345", "000000000002222222222");
        await PayrollTests.ContractAsync(c, ramesh.Id, new DateOnly(2024, 3, 1), ("BASIC", 3000), ("HOUSING", 1000), ("TRANSPORT", 500));
        await PayrollTests.ContractAsync(c, maria.Id, new DateOnly(2024, 3, 1), ("BASIC", 2500), ("HOUSING", 800));
        await c.PostAsync<JsonElement>($"/api/hr/employees/{ramesh.Id}/documents", new { docType = "Qid", docNo = "28435612345", expiryDate = Today.AddDays(20) });
        await c.PostAsync<JsonElement>($"/api/hr/employees/{maria.Id}/documents", new { docType = "Passport", docNo = "P999", expiryDate = Today.AddDays(500) });

        // A draft payroll is invisible; a posted one shows up.
        var run = await c.PostAsync<PayrollTests.Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });

        var access = await c.PostAsync<Access>($"/api/hr/employees/{ramesh.Id}/portal-access", new { });
        access.Username.ShouldBe("e001");
        access.TemporaryPassword.Length.ShouldBe(10);
        (await c.GetAsync<JsonElement>($"/api/hr/employees/{ramesh.Id}")).GetProperty("hasPortalAccess").GetBoolean().ShouldBeTrue();

        var (me, login) = await SignInAsync(t.Code, "E001", access.TemporaryPassword);
        login.MustChangePassword.ShouldBeTrue();
        login.Permissions.ShouldBe(["ess.self:use"]);

        (await me.GetAsync<List<MyPayslip>>("/api/me/payslips")).ShouldBeEmpty();
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/approve", new { });
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/post", new { });
        var payslips = await me.GetAsync<List<MyPayslip>>("/api/me/payslips");
        payslips.Count.ShouldBe(1);
        payslips[0].Gross.ShouldBe(4500m);
        (await me.GetAsync<JsonElement>($"/api/me/payslips/{payslips[0].Id}")).GetProperty("lines").GetArrayLength().ShouldBe(3);
        var pdf = await me.GetAsync($"/api/me/payslips/{payslips[0].Id}/pdf");
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");

        // Maria's payslip id is not reachable through Ramesh's sign-in, and HR screens are closed to him.
        var detail = await c.GetAsync<PayrollTests.RunDetail>($"/api/hr/payroll-runs/{run.Id}");
        var mariaSlip = detail.Payslips.Single(p => p.EmpNo == "E002");
        (await me.GetAsync($"/api/me/payslips/{mariaSlip.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.GetAsync($"/api/me/payslips/{mariaSlip.Id}/pdf")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.GetAsync("/api/hr/employees")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await me.GetAsync($"/api/hr/payroll-runs/{run.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var profile = await me.GetAsync<MyProfile>("/api/me/profile");
        profile.EmpNo.ShouldBe("E001");
        profile.Qid.ShouldBe("•••••••2345");
        profile.MonthlySalary.ShouldBe(4500m);

        var documents = await me.GetAsync<List<MyDocument>>("/api/me/documents");
        documents.Count.ShouldBe(1);
        documents[0].Status.ShouldBe("Expiring");
        documents[0].DaysLeft.ShouldBe(20);

        // Leave: apply, cancel while pending; an approved request can only be cancelled by HR.
        var first = await me.PostAsync<JsonElement>("/api/me/leave",
            new { leaveTypeCode = "ANNUAL", fromDate = Today.AddDays(40), toDate = Today.AddDays(42), reason = "Family visit" });
        var firstId = first.GetProperty("id").GetGuid();
        (await me.PostJsonAsync($"/api/me/leave/{firstId}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var second = (await me.PostAsync<JsonElement>("/api/me/leave",
            new { leaveTypeCode = "ANNUAL", fromDate = Today.AddDays(60), toDate = Today.AddDays(61) })).GetProperty("id").GetGuid();
        await c.PostAsync<JsonElement>($"/api/hr/leave-requests/{second}/approve", new { });
        var refused = await me.PostJsonAsync($"/api/me/leave/{second}/cancel", new { });
        (await refused.ProblemAsync()).GetProperty("code").GetString().ShouldBe("hr.leave_not_pending");
        var leave = await me.GetAsync<MyLeave>("/api/me/leave");
        leave.Requests.Select(r => r.Status).Order().ShouldBe(["Approved", "Cancelled"]);

        var certificate = await me.GetAsync("/api/me/salary-certificate.pdf?addressedTo=QNB");
        certificate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await certificate.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public async Task Temporary_password_must_be_changed_and_revoked_access_stops_sign_in()
    {
        var t = await fixture.CreateTenantAsync();
        var e = await PayrollTests.HireAsync(t.Client, "E050", "Sita Rai", "NP", new DateOnly(2024, 1, 1), "28435655555", "000000000005555555555");
        var access = await t.Client.PostAsync<Access>($"/api/hr/employees/{e.Id}/portal-access", new { });

        var (me, _) = await SignInAsync(t.Code, "e050", access.TemporaryPassword);
        var weak = await me.PostJsonAsync("/api/auth/password", new { currentPassword = access.TemporaryPassword, newPassword = "short" });
        (await weak.ProblemAsync()).GetProperty("code").GetString().ShouldBe("auth.weak_password");
        (await me.PostJsonAsync("/api/auth/password", new { currentPassword = access.TemporaryPassword, newPassword = "My-Own-Password-1" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var (_, again) = await SignInAsync(t.Code, "E050", "My-Own-Password-1");
        again.MustChangePassword.ShouldBeFalse();

        (await t.Client.DeleteAsync($"/api/hr/employees/{e.Id}/portal-access")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var blocked = await fixture.Factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { tenantCode = t.Code, email = "e050", password = "My-Own-Password-1" }, ErpFixture.Json);
        blocked.IsSuccessStatusCode.ShouldBeFalse();

        // Granting again revives the same sign-in with a fresh temporary password.
        var renewed = await t.Client.PostAsync<Access>($"/api/hr/employees/{e.Id}/portal-access", new { });
        renewed.Username.ShouldBe("e050");
        var (_, revived) = await SignInAsync(t.Code, "e050", renewed.TemporaryPassword);
        revived.MustChangePassword.ShouldBeTrue();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class BackOfficeTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private async Task<HttpClient> AdminAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@vendor.qa";
        var key = fixture.Factory.CreateClient();
        key.DefaultRequestHeaders.Add("X-Platform-Key", ErpFixture.PlatformKey);
        (await key.PostAsJsonAsync("/api/platform/admins", new { email, displayName = "Owner of the ERP", password = "Admin-Password-2026" }, ErpFixture.Json))
            .EnsureSuccessStatusCode();
        var client = fixture.Factory.CreateClient();
        var login = await client.PostAsync<JsonElement>("/api/backoffice/auth/login", new { email, password = "Admin-Password-2026" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        return client;
    }

    [Fact]
    public async Task The_back_office_licenses_modules_bills_customers_and_suspends_and_reactivates_on_payment()
    {
        var admin = await AdminAsync();
        // A customer's own token never opens the back-office.
        var other = await fixture.CreateTenantAsync();
        (await other.Client.GetAsync("/api/backoffice/summary")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var code = $"abaya-{Guid.NewGuid():N}"[..20];
        var customer = await admin.PostAsync<JsonElement>("/api/backoffice/customers", new
        {
            code, nameEn = "Al Aziziya Abayas", nameAr = "عبايات العزيزية", adminEmail = "owner@abaya.qa", adminPassword = "Owner-Password-2026",
            adminNameEn = "Owner", adminNameAr = "المالك", planCode = "SHOP", billingCycle = "Monthly", businessType = "tailoring",
            contactName = "Umm Ahmed", contactPhone = "55551234", area = "Al Aziziya"
        });
        var id = customer.GetProperty("customer").GetProperty("id").GetGuid();
        customer.GetProperty("subscription").GetProperty("price").GetDecimal().ShouldBe(199m);
        var first = customer.GetProperty("invoices").EnumerateArray().Single();
        first.GetProperty("amount").GetDecimal().ShouldBe(199m);
        customer.GetProperty("usage").GetProperty("ownerEmail").GetString().ShouldBe("owner@abaya.qa");

        // The Shop plan has no purchasing: the module is off for the customer and they cannot switch it on.
        var shop = await fixture.LoginAsync(code, "owner@abaya.qa", "Owner-Password-2026");
        var modules = await shop.GetAsync<List<JsonElement>>("/api/core/modules");
        modules.Single(m => m.GetProperty("code").GetString() == "purchasing").GetProperty("licensed").GetBoolean().ShouldBeFalse();
        (await shop.GetAsync("/api/purchasing/suppliers")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await shop.GetAsync("/api/sales/customers")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await shop.PutAsJsonAsync("/api/core/modules", new { disabled = Array.Empty<string>() }, ErpFixture.Json)).EnsureSuccessStatusCode();
        (await shop.GetAsync("/api/purchasing/suppliers")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Upgrade: purchasing added on top of the plan; it works at once.
        var plans = await admin.GetAsync<List<JsonElement>>("/api/backoffice/plans");
        var shopPlan = plans.Single(p => p.GetProperty("code").GetString() == "SHOP");
        var licensed = shopPlan.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!).Append("purchasing").ToArray();
        await admin.PutAsync<JsonElement>($"/api/backoffice/customers/{id}/subscription", new
        {
            planId = shopPlan.GetProperty("id").GetGuid(), modules = licensed, billingCycle = "Monthly", price = 249m, autoSuspend = true, graceDays = 14
        });
        (await shop.GetAsync("/api/purchasing/suppliers")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Payment of the first invoice.
        var paid = await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/payments", new
        {
            amount = 199m, paidOn = Today, method = "BankTransfer", reference = "TRX-1"
        });
        paid.GetProperty("customer").GetProperty("balance").GetDecimal().ShouldBe(0m);
        paid.GetProperty("subscription").GetProperty("status").GetString().ShouldBe("Active");
        (await (await admin.PostAsJsonAsync($"/api/backoffice/customers/{id}/payments", new { amount = 1m, paidOn = Today, method = "Cash" }, ErpFixture.Json))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("platform.payment_exceeds");

        // An invoice 20 days overdue (beyond the 14 grace days): suspended — the customer can read but not write.
        var overdue = await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/invoices", new
        {
            description = "Setup and training", amount = 300m, issueDate = Today.AddDays(-27), dueDate = Today.AddDays(-20)
        });
        overdue.GetProperty("subscription").GetProperty("status").GetString().ShouldBe("Suspended");
        overdue.GetProperty("customer").GetProperty("overdue").GetDecimal().ShouldBe(300m);
        (await shop.GetAsync("/api/sales/customers")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await shop.PostJsonAsync("/api/sales/customers", new { code = "C1", nameEn = "Walk-in", nameAr = "عميل", paymentTermsDays = 0 }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("tenant.suspended");

        await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/payments", new { amount = 300m, paidOn = Today, method = "Fawran" });
        (await shop.PostJsonAsync("/api/sales/customers", new { code = "C1", nameEn = "Walk-in", nameAr = "عميل", paymentTermsDays = 0 })).EnsureSuccessStatusCode();

        // Manual suspension, owner password reset, and the summary.
        await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/suspend", new { });
        (await shop.PostJsonAsync("/api/sales/customers", new { code = "C2", nameEn = "B", nameAr = "ب", paymentTermsDays = 0 })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/reactivate", new { });
        var reset = await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/reset-owner-password", new { });
        await fixture.LoginAsync(code, "owner@abaya.qa", reset.GetProperty("temporaryPassword").GetString()!);

        var summary = await admin.GetAsync<JsonElement>("/api/backoffice/summary");
        summary.GetProperty("customers").GetInt32().ShouldBeGreaterThan(0);
        summary.GetProperty("collectedThisMonth").GetDecimal().ShouldBeGreaterThanOrEqualTo(499m);
        var rows = await admin.GetAsync<List<JsonElement>>($"/api/backoffice/customers?search={code}");
        rows.Single().GetProperty("monthlyValue").GetDecimal().ShouldBe(249m);

        // Closing: no sign-in at all.
        await admin.PostAsync<JsonElement>($"/api/backoffice/customers/{id}/close", new { });
        var login = await fixture.Factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { tenantCode = code, email = "owner@abaya.qa", password = reset.GetProperty("temporaryPassword").GetString() }, ErpFixture.Json);
        login.IsSuccessStatusCode.ShouldBeFalse();
    }
}

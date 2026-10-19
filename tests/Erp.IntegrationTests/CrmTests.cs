using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class CrmTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Lead(Guid Id, string LeadNo, string Status, Guid? CustomerId);
    public sealed record Opportunity(Guid Id, string Stage, decimal ExpectedValue, int Probability, decimal Weighted);
    public sealed record Stage(string StageName, int Count, decimal Value, decimal Weighted);
    public sealed record Pipeline(decimal OpenValue, decimal Weighted, int WonThisMonth, decimal WinRate);
    public sealed record Activity(Guid Id, string Subject, bool Done, bool Overdue, string? RegardingEn);
    public sealed record CommissionRow(Guid UserId, string Basis, decimal Revenue, decimal GrossMargin, decimal Commission);

    [Fact]
    public async Task Lead_becomes_a_customer_opportunities_build_the_pipeline_and_commission_follows_the_salesperson()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;

        var lead = await c.PostAsync<Lead>("/api/sales/crm/leads", new
        {
            companyName = "Pearl Interiors", contactName = "Sara", phone = "+97455500011", source = "Exhibition", status = "New"
        });
        lead.LeadNo.ShouldStartWith("LEAD");
        (await (await c.PutAsJsonAsync($"/api/sales/crm/leads/{lead.Id}", new { companyName = "Pearl Interiors", source = "Exhibition", status = "Lost" },
            ErpFixture.Json)).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.lost_reason");

        var activityId = await c.PostAsync<Guid>("/api/sales/crm/activities", new
        {
            leadId = lead.Id, kind = "Call", subject = "Follow up after the exhibition", dueDate = Today.AddDays(-1)
        });
        var activities = await c.GetAsync<List<Activity>>("/api/sales/crm/activities");
        activities.Single().Overdue.ShouldBeTrue();
        activities.Single().RegardingEn.ShouldBe("Pearl Interiors");
        (await c.PostJsonAsync($"/api/sales/crm/activities/{activityId}/done", new { })).EnsureSuccessStatusCode();
        (await c.GetAsync<List<Activity>>("/api/sales/crm/activities")).ShouldBeEmpty();

        // Conversion creates the customer (owned by the lead owner) and its first opportunity.
        var converted = await c.PostAsync<JsonElement>($"/api/sales/crm/leads/{lead.Id}/convert", new
        {
            customerCode = "C-PEARL", nameAr = "لآلئ للديكور", opportunityTitle = "Villa fit-out tiles", expectedValue = 80_000m
        });
        var customerId = converted.GetProperty("customerId").GetGuid();
        var opportunityId = converted.GetProperty("opportunityId").GetGuid();
        (await c.GetAsync<JsonElement>($"/api/sales/customers/{customerId}")).GetProperty("salespersonUserId").GetGuid().ShouldBe(t.AdminUserId);

        var second = await c.PostAsync<Opportunity>("/api/sales/crm/opportunities", new
        {
            title = "Showroom flooring", customerId, stage = "Proposal", expectedValue = 20_000m
        });
        second.Probability.ShouldBe(50);
        var pipeline = await c.GetAsync<Pipeline>("/api/sales/crm/pipeline");
        pipeline.OpenValue.ShouldBe(100_000m);
        pipeline.Weighted.ShouldBe(80_000m * 0.25m + 20_000m * 0.5m);

        await c.PutAsync<Opportunity>($"/api/sales/crm/opportunities/{opportunityId}", new
        {
            title = "Villa fit-out tiles", customerId, stage = "Won", expectedValue = 80_000m
        });
        pipeline = await c.GetAsync<Pipeline>("/api/sales/crm/pipeline");
        pipeline.WonThisMonth.ShouldBe(1);
        pipeline.WinRate.ShouldBe(100m);

        // 5% of revenue for the salesperson: a 10,000 invoice earns 500.
        (await c.PutAsJsonAsync("/api/sales/commissions/rates", new { userId = t.AdminUserId, basis = "Revenue", percent = 5m }, ErpFixture.Json))
            .EnsureSuccessStatusCode();
        var invoice = await c.PostAsync<JsonElement>("/api/sales/invoices", new
        {
            customerId, invoiceDate = Today, lines = new[] { new { description = "Design consultation", quantity = 1m, unitPrice = 10_000m } }
        });
        await c.PostAsync<JsonElement>($"/api/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
        var report = await c.GetAsync<List<CommissionRow>>($"/api/sales/commissions/report?year={Today.Year}&month={Today.Month}");
        var mine = report.Single(r => r.UserId == t.AdminUserId);
        mine.Revenue.ShouldBe(10_000m);
        mine.Commission.ShouldBe(500m);
    }
}

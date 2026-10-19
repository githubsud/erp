using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class ApprovalTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Step(int StepNo, string RoleCode, string? Decision, string? Comment);
    public sealed record Request(Guid Id, string DocType, Guid DocId, string DocNo, decimal Amount, string Status, int CurrentStep, List<Step> Steps, bool CanDecide);
    public sealed record Pr(Guid Id, string PrNo, string Status, decimal EstimatedTotal, Guid? PurchaseOrderId);

    private async Task<HttpClient> UserAsync(TenantSession t, string email, string role)
    {
        await (await t.Client.PostJsonAsync("/api/core/users", new
        {
            email, displayNameEn = email, password = "User-Password-123", preferredLanguage = "en", roles = new[] { role }
        })).EnsureSuccessAsync();
        return await fixture.LoginAsync(t.Code, email, "User-Password-123");
    }

    [Fact]
    public async Task Purchase_orders_need_one_or_two_approvals_by_amount_and_follow_segregation_of_duties()
    {
        var t = await fixture.CreateTenantAsync();
        var (main, _, _) = await InventoryTests.DefaultsAsync(t.Client);
        var cement = await InventoryTests.CreateItemAsync(t.Client, "CEM");
        var supplier = await PurchasingTests.SupplierAsync(t.Client, "S1");
        (await t.Client.PutAsJsonAsync("/api/approvals/rules/purchase_order", new[]
        {
            new { minAmount = 10_000m, roleCode = "general_manager" }, new { minAmount = 50_000m, roleCode = "accountant" }
        }, ErpFixture.Json)).EnsureSuccessStatusCode();
        var buyer = await UserAsync(t, "buyer@t.qa", "purchaser");
        var gm = await UserAsync(t, "gm@t.qa", "general_manager");
        var accountant = await UserAsync(t, "acc@t.qa", "accountant");

        var one = await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 1m, 49_999m));
        var two = await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 1m, 50_000m));
        one.Status.ShouldBe("PendingApproval");
        two.Status.ShouldBe("PendingApproval");

        var gmInbox = await gm.GetAsync<List<Request>>("/api/approvals/inbox");
        gmInbox.Select(r => r.DocId).ShouldBe([one.Id, two.Id], ignoreOrder: true);
        gmInbox.Single(r => r.DocId == one.Id).Steps.Count.ShouldBe(1);
        gmInbox.Single(r => r.DocId == two.Id).Steps.Count.ShouldBe(2);
        (await accountant.GetAsync<List<Request>>("/api/approvals/inbox")).ShouldBeEmpty();
        (await buyer.GetAsync<List<Request>>("/api/approvals/inbox")).ShouldBeEmpty();

        // 49,999: one approval completes it.
        (await gm.PostJsonAsync($"/api/approvals/{gmInbox.Single(r => r.DocId == one.Id).Id}/approve", new { comment = "OK" })).EnsureSuccessStatusCode();
        (await t.Client.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{one.Id}")).Status.ShouldBe("Approved");

        // 50,000: the GM's approval moves it to the accountant; the buyer cannot decide it.
        var twoRequest = gmInbox.Single(r => r.DocId == two.Id).Id;
        (await gm.PostJsonAsync($"/api/approvals/{twoRequest}/approve", new { })).EnsureSuccessStatusCode();
        (await t.Client.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{two.Id}")).Status.ShouldBe("PendingApproval");
        (await gm.PostJsonAsync($"/api/approvals/{twoRequest}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await buyer.PostJsonAsync($"/api/approvals/{twoRequest}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await accountant.GetAsync<List<Request>>("/api/approvals/inbox")).Single().CurrentStep.ShouldBe(2);
        (await accountant.PostJsonAsync($"/api/approvals/{twoRequest}/approve", new { })).EnsureSuccessStatusCode();
        (await t.Client.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{two.Id}")).Status.ShouldBe("Approved");

        var history = await t.Client.GetAsync<List<Request>>($"/api/approvals/requests?docType=purchase_order&docId={two.Id}");
        history.Single().Steps.Select(s => s.Decision).ShouldBe(["Approved", "Approved"]);

        // Rejection needs a reason and sends the order back to draft.
        var three = await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 2m, 6_000m));
        var threeRequest = (await gm.GetAsync<List<Request>>("/api/approvals/inbox")).Single().Id;
        (await (await gm.PostJsonAsync($"/api/approvals/{threeRequest}/reject", new { comment = "" })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("core.approval_reason");
        (await gm.PostJsonAsync($"/api/approvals/{threeRequest}/reject", new { comment = "Get a second quotation" })).EnsureSuccessStatusCode();
        (await t.Client.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{three.Id}")).Status.ShouldBe("Draft");

        // The owner may approve anything, but not their own order unless the company allows it.
        var own = await PurchasingTests.OrderAsync(t.Client, supplier, main, (cement, 1m, 20_000m));
        (await t.Client.PostJsonAsync($"/api/purchasing/orders/{own.Id}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await t.Client.PutAsJsonAsync("/api/approvals/settings", new { allowSelfApproval = true }, ErpFixture.Json)).EnsureSuccessStatusCode();
        (await t.Client.PostAsync<PurchasingTests.Order>($"/api/purchasing/orders/{own.Id}/approve", new { })).Status.ShouldBe("Approved");

        // Cancelling an order waiting for approval withdraws the request.
        var four = await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 1m, 15_000m));
        await buyer.PostAsync<PurchasingTests.Order>($"/api/purchasing/orders/{four.Id}/cancel", new { });
        (await gm.GetAsync<List<Request>>("/api/approvals/inbox")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delegate_approves_on_behalf_of_an_absent_approver_within_the_same_segregation_rules()
    {
        var t = await fixture.CreateTenantAsync();
        var (main, _, _) = await InventoryTests.DefaultsAsync(t.Client);
        var cement = await InventoryTests.CreateItemAsync(t.Client, "CEM");
        var supplier = await PurchasingTests.SupplierAsync(t.Client, "S1");
        (await t.Client.PutAsJsonAsync("/api/approvals/rules/purchase_order", new[]
        {
            new { minAmount = 10_000m, roleCode = "general_manager" }, new { minAmount = 50_000m, roleCode = "accountant" }
        }, ErpFixture.Json)).EnsureSuccessStatusCode();
        var buyer = await UserAsync(t, "buyer@t.qa", "purchaser");
        var gm = await UserAsync(t, "gm@t.qa", "general_manager");
        var accountant = await UserAsync(t, "acc@t.qa", "accountant");
        var people = await gm.GetAsync<List<JsonElement>>("/api/approvals/delegates");
        Guid Id(string email) => people.Single(p => p.GetProperty("displayNameEn").GetString() == email).GetProperty("id").GetGuid();

        // The GM goes on leave and hands purchase-order approvals to the accountant; the buyer cannot be a delegate for their own orders.
        var delegation = await gm.PostAsync<Guid>("/api/approvals/delegations", new
        {
            delegateUserId = Id("acc@t.qa"), fromDate = Today, toDate = Today.AddDays(10), docType = "purchase_order", reason = "Annual leave"
        });
        await gm.PostAsync<Guid>("/api/approvals/delegations", new { delegateUserId = Id("buyer@t.qa"), fromDate = Today, toDate = Today.AddDays(10) });
        (await (await accountant.PostJsonAsync("/api/approvals/delegations", new
        {
            delegatorUserId = Id("gm@t.qa"), delegateUserId = Id("acc@t.qa"), fromDate = Today, toDate = Today
        })).ProblemAsync()).GetProperty("status").GetInt32().ShouldBe(403);

        var small = await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 1m, 20_000m));
        (await buyer.GetAsync<List<Request>>("/api/approvals/inbox")).ShouldBeEmpty();
        var inbox = await accountant.GetAsync<List<Request>>("/api/approvals/inbox");
        inbox.Single().DocId.ShouldBe(small.Id);
        (await accountant.PostJsonAsync($"/api/approvals/{inbox.Single().Id}/approve", new { comment = "For the GM" })).EnsureSuccessStatusCode();
        (await t.Client.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{small.Id}")).Status.ShouldBe("Approved");
        var history = await t.Client.GetAsync<List<JsonElement>>($"/api/approvals/requests?docType=purchase_order&docId={small.Id}");
        history.Single().GetProperty("steps")[0].GetProperty("onBehalfOfName").GetString().ShouldBe("gm@t.qa");

        // Two steps: having approved step 1 for the GM, the accountant cannot also approve step 2 — the GM's own approval is needed instead.
        var big = await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 1m, 60_000m));
        var bigRequest = (await accountant.GetAsync<List<Request>>("/api/approvals/inbox")).Single().Id;
        (await accountant.PostJsonAsync($"/api/approvals/{bigRequest}/approve", new { })).EnsureSuccessStatusCode();
        (await accountant.GetAsync<List<Request>>("/api/approvals/inbox")).ShouldBeEmpty();
        (await accountant.PostJsonAsync($"/api/approvals/{bigRequest}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Revoked: back to normal.
        (await gm.PostJsonAsync($"/api/approvals/delegations/{delegation}/revoke", new { })).EnsureSuccessStatusCode();
        await PurchasingTests.OrderAsync(buyer, supplier, main, (cement, 1m, 15_000m));
        (await accountant.GetAsync<List<Request>>("/api/approvals/inbox")).ShouldBeEmpty();
        var mine = await gm.GetAsync<List<JsonElement>>("/api/approvals/delegations");
        mine.Count.ShouldBe(2);
        mine.Single(d => d.GetProperty("id").GetGuid() == delegation).GetProperty("active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Purchase_request_is_approved_and_turned_into_a_draft_order()
    {
        var t = await fixture.CreateTenantAsync();
        var (main, _, _) = await InventoryTests.DefaultsAsync(t.Client);
        var cement = await InventoryTests.CreateItemAsync(t.Client, "CEM");
        var supplier = await PurchasingTests.SupplierAsync(t.Client, "S1");
        var store = await UserAsync(t, "store@t.qa", "storekeeper");
        var gm = await UserAsync(t, "gm@t.qa", "general_manager");

        // No rule for requests by default: submitting approves it.
        var pr = await store.PostAsync<Pr>("/api/purchasing/requests", new
        {
            requestDate = Today, requiredDate = Today.AddDays(7), warehouseId = main, notes = "Site B",
            lines = new[] { new { itemId = cement, quantity = 40m, estimatedPrice = 18.5m } }
        });
        pr.EstimatedTotal.ShouldBe(740m);
        (await store.PostAsync<Pr>($"/api/purchasing/requests/{pr.Id}/submit", new { })).Status.ShouldBe("Approved");

        // With a rule, the GM approves it from the inbox.
        (await t.Client.PutAsJsonAsync("/api/approvals/rules/purchase_request", new[] { new { minAmount = 0m, roleCode = "general_manager" } },
            ErpFixture.Json)).EnsureSuccessStatusCode();
        var second = await store.PostAsync<Pr>("/api/purchasing/requests", new
        {
            requestDate = Today, warehouseId = main, lines = new[] { new { itemId = cement, quantity = 10m, estimatedPrice = 18m } }
        });
        (await store.PostAsync<Pr>($"/api/purchasing/requests/{second.Id}/submit", new { })).Status.ShouldBe("PendingApproval");
        (await store.PostJsonAsync($"/api/purchasing/requests/{second.Id}/convert", new { supplierId = supplier })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var request = (await gm.GetAsync<List<Request>>("/api/approvals/inbox")).Single();
        request.DocNo.ShouldBe(second.PrNo);
        (await gm.PostJsonAsync($"/api/approvals/{request.Id}/approve", new { })).EnsureSuccessStatusCode();
        (await t.Client.GetAsync<Pr>($"/api/purchasing/requests/{second.Id}")).Status.ShouldBe("Approved");

        var converted = await t.Client.PostAsync<JsonElement>($"/api/purchasing/requests/{pr.Id}/convert", new { supplierId = supplier });
        var orderId = converted.GetProperty("purchaseOrderId").GetGuid();
        var order = await t.Client.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{orderId}");
        order.Status.ShouldBe("Draft");
        order.Total.ShouldBe(740m);
        order.Lines.Single().Quantity.ShouldBe(40m);
        var done = await t.Client.GetAsync<Pr>($"/api/purchasing/requests/{pr.Id}");
        done.Status.ShouldBe("Ordered");
        done.PurchaseOrderId.ShouldBe(orderId);
        (await t.Client.PostJsonAsync($"/api/purchasing/requests/{pr.Id}/convert", new { supplierId = supplier })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}

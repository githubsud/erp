using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>Till limits: a cashier's discount (or lower price) is capped; past the cap a manager approves with a PIN.</summary>
[Collection(ErpCollection.Name)]
public sealed class PosLimitTests(ErpFixture fixture)
{
    private const string Password = "User-Password-123";

    private static async Task<Guid> AddUserAsync(HttpClient owner, string email, string role) =>
        (await owner.PostAsync<JsonElement>("/api/core/users",
            new { email, displayNameEn = email, password = Password, preferredLanguage = "en", roles = new[] { role } })).GetProperty("id").GetGuid();

    private static async Task<string?> CodeAsync(Task<HttpResponseMessage> call) => (await (await call).ProblemAsync()).GetProperty("code").GetString();

    [Fact]
    public async Task A_discount_past_the_cashier_limit_needs_a_managers_pin_once_per_sale()
    {
        var t = await fixture.CreateTenantAsync();
        var owner = t.Client;
        var accounts = await owner.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(owner);
        var item = (await owner.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "SOAP", nameEn = "Soap", nameAr = "صابون", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 10m
        })).Id;
        (await owner.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = FinanceTests.Today, reason = "Opening", lines = new[] { new { itemId = item, quantity = 100m, unitCost = 4m } }
        })).EnsureSuccessStatusCode();
        var register = await owner.PostAsync<Guid>("/api/sales/pos/registers", new
        {
            code = "T1", nameEn = "Till 1", nameAr = "صندوق 1", warehouseId = main, cashAccountId = A("1110"), cardAccountId = A("1115")
        });
        var gmId = await AddUserAsync(owner, $"gm@{t.Code}.qa", "general_manager");
        await AddUserAsync(owner, $"cashier@{t.Code}.qa", "salesperson");
        var gm = await fixture.LoginAsync(t.Code, $"gm@{t.Code}.qa", Password);
        var cashier = await fixture.LoginAsync(t.Code, $"cashier@{t.Code}.qa", Password);

        var policy = await cashier.GetAsync<JsonElement>("/api/sales/pos/policy");
        policy.GetProperty("maxDiscountPercent").GetDecimal().ShouldBe(10m);
        policy.GetProperty("canOverride").GetBoolean().ShouldBeFalse();
        (await owner.GetAsync<JsonElement>("/api/sales/pos/policy")).GetProperty("canOverride").GetBoolean().ShouldBeTrue();

        var shift = (await cashier.PostAsync<JsonElement>("/api/sales/pos/shifts", new { registerId = register, openingFloat = 0m })).GetProperty("id").GetGuid();
        Task<HttpResponseMessage> Sell(HttpClient c, decimal discount, decimal? unitPrice = null, Guid? approvalId = null) =>
            c.PostJsonAsync("/api/sales/pos/sales", new
            {
                clientId = Guid.NewGuid(), shiftId = shift, lines = new[] { new { itemId = item, quantity = 1m, unitPrice, discountPercent = discount } },
                cash = 20m, card = 0m, approvalId
            });

        // Up to the limit is the cashier's call; a higher price is fine too.
        (await Sell(cashier, 10m)).EnsureSuccessStatusCode();
        (await Sell(cashier, 0m, 12m)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Past it, by discount or by a lower price, a manager must approve.
        (await CodeAsync(Sell(cashier, 15m))).ShouldBe("sal.pos_needs_approval");
        (await CodeAsync(Sell(cashier, 0m, 8m))).ShouldBe("sal.pos_needs_approval");
        // Someone with the override permission selling at the till needs nobody.
        (await Sell(owner, 50m)).EnsureSuccessStatusCode();
        // The till sees what is left (100 opening, 3 sold) to show it on the item and warn when it runs low.
        var soap = (await cashier.GetAsync<List<JsonElement>>("/api/sales/pos/catalog")).Single(i => i.GetProperty("id").GetGuid() == item);
        soap.GetProperty("onHand").GetDecimal().ShouldBe(97m);

        // PINs: only managers set one, and not an easy one.
        (await cashier.PutJsonAsync("/api/sales/pos/my-pin", new { pin = "4827" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(gm.PutJsonAsync("/api/sales/pos/my-pin", new { pin = "1234" }))).ShouldBe("sal.pos_pin_weak");
        (await CodeAsync(gm.PutJsonAsync("/api/sales/pos/my-pin", new { pin = "12a4" }))).ShouldBe("sal.pos_pin_format");
        (await gm.PutJsonAsync("/api/sales/pos/my-pin", new { pin = "4827" })).EnsureSuccessStatusCode();
        (await owner.PutJsonAsync("/api/sales/pos/my-pin", new { pin = "907153" })).EnsureSuccessStatusCode();
        var approvers = await cashier.GetAsync<List<JsonElement>>("/api/sales/pos/approvers");
        approvers.Select(a => a.GetProperty("userId").GetGuid()).ShouldBe([t.AdminUserId, gmId], ignoreOrder: true);

        Task<HttpResponseMessage> Approve(Guid approver, string pin, decimal discount) =>
            cashier.PostJsonAsync("/api/sales/pos/approvals", new { shiftId = shift, approverUserId = approver, pin, discount });

        // 15% off a 10.00 soap takes 1.50 off: approved with the owner's PIN, recorded on the sale, good for one sale only.
        var approval = await (await Approve(t.AdminUserId, "907153", 1.5m)).ReadAsync<JsonElement>();
        var approvalId = approval.GetProperty("id").GetGuid();
        (await Sell(cashier, 15m, approvalId: approvalId)).EnsureSuccessStatusCode();
        (await CodeAsync(Sell(cashier, 15m, approvalId: approvalId))).ShouldBe("sal.pos_approval_used");
        // The approval covers what the manager saw, not a bigger discount.
        var small = (await (await Approve(t.AdminUserId, "907153", 1m)).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await CodeAsync(Sell(cashier, 15m, approvalId: small))).ShouldBe("sal.pos_approval_short");

        // Five wrong PINs lock the manager's PIN, even for the right one.
        for (var i = 0; i < 5; i++) (await CodeAsync(Approve(gmId, "0000", 1m))).ShouldBe("sal.pos_wrong_pin");
        (await CodeAsync(Approve(gmId, "4827", 1m))).ShouldBe("sal.pos_pin_locked");

        // The losses report names the manager who approved.
        var leaks = await owner.GetAsync<JsonElement>("/api/sales/reports/leakage?days=1");
        var approved = leaks.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "approved_discounts");
        approved.GetProperty("count").GetInt32().ShouldBe(1);
        approved.GetProperty("amount").GetDecimal().ShouldBe(1.5m);

        // The owner raises the limit: 15% is now the cashier's call.
        await owner.PutAsync<JsonElement>("/api/sales/settings", new { creditControl = "Block", quotationValidityDays = 30, posMaxDiscountPercent = 20m });
        (await Sell(cashier, 15m)).EnsureSuccessStatusCode();
        // Leaving the limit out of the settings keeps it.
        (await owner.PutAsync<JsonElement>("/api/sales/settings", new { creditControl = "Block", quotationValidityDays = 30 }))
            .GetProperty("posMaxDiscountPercent").GetDecimal().ShouldBe(20m);
    }
}

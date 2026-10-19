using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class TailoringTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task An_abaya_order_takes_a_deposit_and_on_delivery_is_invoiced_with_the_deposit_applied()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        var cash = accounts.Single(a => a.Code == "1110").Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var crepe = (await c.PostAsync<JsonElement>("/api/inventory/items", new
        {
            sku = "CREPE", nameEn = "Black crepe fabric (m)", nameAr = "قماش كريب أسود (م)", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 30m
        })).GetProperty("id").GetGuid();
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new[] { new { itemId = crepe, quantity = 50m, unitCost = 12m } }
        })).EnsureSuccessStatusCode();

        // Walk-in: 2 abayas at 250 and 3 m of fabric at 30 = 590; 200 deposit in cash.
        var order = await c.PostAsync<JsonElement>("/api/tailoring/orders", new
        {
            customerName = "Mariam Al-Kuwari", phone = "5555 1234", orderDate = Today, dueDate = Today.AddDays(10), fittingDate = Today.AddDays(6),
            garment = "Abaya", measurements = new Dictionary<string, string> { ["length"] = "142", ["shoulder"] = "39", ["sleeve"] = "58" },
            styleNotes = "Wide sleeves, stones on the cuffs", tailorName = "Rahim",
            lines = new object[]
            {
                new { description = "Abaya, embroidered cuffs", quantity = 2m, unitPrice = 250m },
                new { itemId = crepe, quantity = 3m, unitPrice = 30m }
            },
            deposit = new { amount = 200m, accountId = cash, method = "Cash" }
        });
        var id = order.GetProperty("id").GetGuid();
        order.GetProperty("total").GetDecimal().ShouldBe(590m);
        order.GetProperty("paid").GetDecimal().ShouldBe(200m);
        order.GetProperty("whatsAppPhone").GetString().ShouldBe("97455551234");
        (await ClosingAsync(c, "1110")).ShouldBe(200m);
        (await ClosingAsync(c, "1130")).ShouldBe(-200m);   // the deposit is a credit on the customer's account

        // The same phone finds the same customer, and the measurements are remembered.
        var customerId = order.GetProperty("customerId").GetGuid();
        var profiles = await c.GetAsync<JsonElement>($"/api/tailoring/customers/{customerId}/measurements");
        profiles.GetProperty("Abaya").GetProperty("length").GetString().ShouldBe("142");
        var second = await c.PostAsync<JsonElement>("/api/tailoring/orders", new
        {
            customerName = "Mariam", phone = "+974 5555 1234", orderDate = Today, dueDate = Today.AddDays(20), garment = "Dress",
            lines = new[] { new { description = "Evening dress", quantity = 1m, unitPrice = 400m } }
        });
        second.GetProperty("customerId").GetGuid().ShouldBe(customerId);

        var board = await c.GetAsync<JsonElement>("/api/tailoring/board");
        board.GetProperty("open").GetInt32().ShouldBe(2);
        board.GetProperty("depositsHeld").GetDecimal().ShouldBe(200m);

        await c.PostAsync<JsonElement>($"/api/tailoring/orders/{id}/status", new { status = "InProgress" });
        var ready = await c.PostAsync<JsonElement>($"/api/tailoring/orders/{id}/status", new { status = "Ready" });
        ready.GetProperty("readyOn").GetString().ShouldNotBeNull();
        (await (await c.PostJsonAsync($"/api/tailoring/orders/{id}/deliver", new
        {
            date = Today, warehouseId = main, finalPayment = new { amount = 500m, accountId = cash, method = "Cash" }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.tailoring_overpaid");

        var delivered = await c.PostAsync<JsonElement>($"/api/tailoring/orders/{id}/deliver", new
        {
            date = Today, warehouseId = main, finalPayment = new { amount = 390m, accountId = cash, method = "Cash" }
        });
        delivered.GetProperty("status").GetString().ShouldBe("Delivered");
        delivered.GetProperty("balance").GetDecimal().ShouldBe(0m);
        var invoice = await c.GetAsync<JsonElement>($"/api/sales/invoices/{delivered.GetProperty("salesInvoiceId").GetGuid()}");
        invoice.GetProperty("total").GetDecimal().ShouldBe(590m);
        invoice.GetProperty("status").GetString().ShouldBe("Paid");

        (await ClosingAsync(c, "1110")).ShouldBe(590m);
        (await ClosingAsync(c, "1130")).ShouldBe(0m);
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.TotalValue.ShouldBe(47m * 12m);
        valuation.Difference.ShouldBe(0m);
        var aging = await c.GetAsync<JsonElement>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(aging.GetProperty("ledgerBalance").GetDecimal());
    }
}

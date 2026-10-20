using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class WorkshopTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task A_job_card_takes_an_advance_changes_its_estimate_and_on_delivery_is_invoiced_with_parts_leaving_stock()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var cash = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1110").Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var pads = (await c.PostAsync<JsonElement>("/api/inventory/items", new
        {
            sku = "PADS", nameEn = "Brake pads (set)", nameAr = "فحمات فرامل", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 160m
        })).GetProperty("id").GetGuid();
        var labour = (await c.PostAsync<JsonElement>("/api/inventory/items", new
        {
            sku = "L-BRAKE", nameEn = "Brake job labour", nameAr = "أجرة فرامل", itemType = "Service", itemGroupId = group, uomId = pcs, salesPrice = 120m
        })).GetProperty("id").GetGuid();
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new[] { new { itemId = pads, quantity = 10m, unitCost = 70m } }
        })).EnsureSuccessStatusCode();

        // A walk-in leaves the car with a 100 advance before the estimate is ready.
        var job = await c.PostAsync<JsonElement>("/api/workshop/jobs", new
        {
            customerName = "Rashid Al-Kaabi", phone = "3344 5566", plateNo = "123 456", make = "Toyota", model = "Land Cruiser", year = 2019, color = "White",
            receivedOn = Today, promisedOn = Today.AddDays(1), mileage = 84500, complaint = "Noise when braking", technicianName = "Senthil",
            deposit = new { amount = 100m, accountId = cash, method = "Cash" }
        });
        var id = job.GetProperty("id").GetGuid();
        job.GetProperty("plateNo").GetString().ShouldBe("123456");
        job.GetProperty("vehicle").GetString().ShouldBe("Toyota Land Cruiser 2019 White");
        job.GetProperty("paid").GetDecimal().ShouldBe(100m);
        job.GetProperty("whatsAppPhone").GetString().ShouldBe("97433445566");

        // Estimate: 1 set of pads 160 + 1.5 hours labour at 120 = 340; the customer approves it.
        job = await c.PutJsonAsync($"/api/workshop/jobs/{id}/lines", new object[]
        {
            new { kind = "Part", itemId = pads, quantity = 1m, unitPrice = 160m },
            new { kind = "Labour", itemId = labour, description = "Replace front pads, clean calipers", quantity = 1.5m, unitPrice = 120m }
        }).ContinueWith(x => x.Result.ReadAsync<JsonElement>()).Unwrap();
        job.GetProperty("total").GetDecimal().ShouldBe(340m);
        await c.PostAsync<JsonElement>($"/api/workshop/jobs/{id}/status", new { status = "AwaitingApproval", diagnosis = "Front pads worn out" });
        var approved = await c.PostAsync<JsonElement>($"/api/workshop/jobs/{id}/status", new { status = "InProgress" });
        approved.GetProperty("approvedOn").GetString().ShouldBe(Today.ToString("yyyy-MM-dd"));
        var board = await c.GetAsync<JsonElement>("/api/workshop/board");
        board.GetProperty("open").GetInt32().ShouldBe(1);
        board.GetProperty("depositsHeld").GetDecimal().ShouldBe(100m);
        (await ClosingAsync(c, "1130")).ShouldBe(-100m);   // the advance is a credit on the customer's account

        await c.PostAsync<JsonElement>($"/api/workshop/jobs/{id}/status", new { status = "Ready" });
        (await (await c.PostJsonAsync($"/api/workshop/jobs/{id}/deliver", new
        {
            date = Today, warehouseId = main, finalPayment = new { amount = 300m, accountId = cash, method = "Cash" }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.job_overpaid");
        var delivered = await c.PostAsync<JsonElement>($"/api/workshop/jobs/{id}/deliver", new
        {
            date = Today, warehouseId = main, finalPayment = new { amount = 240m, accountId = cash, method = "Cash" }
        });
        delivered.GetProperty("status").GetString().ShouldBe("Delivered");
        delivered.GetProperty("balance").GetDecimal().ShouldBe(0m);
        var invoice = await c.GetAsync<JsonElement>($"/api/sales/invoices/{delivered.GetProperty("salesInvoiceId").GetGuid()}");
        invoice.GetProperty("total").GetDecimal().ShouldBe(340m);
        invoice.GetProperty("status").GetString().ShouldBe("Paid");
        (await (await c.PutJsonAsync($"/api/workshop/jobs/{id}/lines", Array.Empty<object>())).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.job_closed");

        (await ClosingAsync(c, "1110")).ShouldBe(340m);
        (await ClosingAsync(c, "1130")).ShouldBe(0m);
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.TotalValue.ShouldBe(9m * 70m);
        valuation.Difference.ShouldBe(0m);

        // The car comes back: its plate brings the owner and the history.
        var again = await c.PostAsync<JsonElement>("/api/workshop/jobs", new { plateNo = "123-456", receivedOn = Today, mileage = 90100, complaint = "Oil change" });
        again.GetProperty("customerId").GetGuid().ShouldBe(job.GetProperty("customerId").GetGuid());
        again.GetProperty("vehicleId").GetGuid().ShouldBe(job.GetProperty("vehicleId").GetGuid());
        var vehicles = await c.GetAsync<List<JsonElement>>("/api/workshop/vehicles?search=123456");
        vehicles.Single().GetProperty("visits").GetInt32().ShouldBe(2);
        vehicles.Single().GetProperty("lastMileage").GetInt32().ShouldBe(90100);
        (await c.PostAsync<JsonElement>($"/api/workshop/jobs/{again.GetProperty("id").GetGuid()}/cancel", new { })).GetProperty("status").GetString().ShouldBe("Cancelled");
    }
}

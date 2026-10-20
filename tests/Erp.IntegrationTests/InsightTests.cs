using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class InsightTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    [Fact]
    public async Task Cash_shortages_heavy_refunds_and_sales_below_cost_show_as_possible_leaks()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var perfume = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "OUD", nameEn = "Oud perfume", nameAr = "عطر عود", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 100m
        })).Id;
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new[] { new { itemId = perfume, quantity = 50m, unitCost = 60m } }
        })).EnsureSuccessStatusCode();
        var register = await c.PostAsync<Guid>("/api/sales/pos/registers", new
        {
            code = "T1", nameEn = "Till 1", nameAr = "صندوق 1", warehouseId = main, cashAccountId = A("1110"), cardAccountId = A("1115")
        });
        var shift = await c.PostAsync<PosTests.Shift>("/api/sales/pos/shifts", new { registerId = register, openingFloat = 0m });

        // 10 perfumes at full price = 1,000; one sold at 50% off (below its cost of 60); 3 of the first sale refunded.
        var sale = await c.PostAsync<PosTests.Sale>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, lines = new[] { new { itemId = perfume, quantity = 10m } }, cash = 1000m, card = 0m
        });
        await c.PostAsync<PosTests.Sale>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, lines = new[] { new { itemId = perfume, quantity = 1m, discountPercent = 50m } }, cash = 50m, card = 0m
        });
        await c.PostAsync<PosTests.Sale>("/api/sales/pos/refunds", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, saleId = sale.Id, lines = new[] { new { saleLineId = sale.Lines[0].Id, quantity = 3m } }
        });
        // Expected cash 1,050 − 300 = 750; only 690 counted.
        await c.PostAsync<JsonElement>($"/api/sales/pos/shifts/{shift.Id}/close", new { countedCash = 690m });

        var report = await c.GetAsync<JsonElement>("/api/sales/reports/leakage?days=7");
        var findings = report.GetProperty("findings").EnumerateArray().ToDictionary(f => f.GetProperty("kind").GetString()!);
        findings["cash_shortage"].GetProperty("amount").GetDecimal().ShouldBe(60m);
        findings["cash_shortage"].GetProperty("severity").GetString().ShouldBe("bad");
        findings["cash_shortage"].GetProperty("person").GetString().ShouldBe("Owner");
        findings["refunds"].GetProperty("amount").GetDecimal().ShouldBe(300m);
        findings["refunds"].GetProperty("percent").GetDecimal().ShouldBe(28.6m);   // 300 of 1,050 sold
        findings["below_cost"].GetProperty("amount").GetDecimal().ShouldBe(10m);   // sold 50, cost 60
        findings.ContainsKey("discounts").ShouldBeFalse();                          // 50 of 1,100 full price = 4.5%, under the 5% line
        report.GetProperty("amountAtRisk").GetDecimal().ShouldBe(70m);

        var tiles = await c.GetAsync<List<JsonElement>>("/api/core/dashboard");
        tiles.Single(x => x.GetProperty("key").GetString() == "sal.leaks").GetProperty("tone").GetString().ShouldBe("bad");
    }

    [Fact]
    public async Task Seasonal_demand_forecasts_from_recent_sales_and_lists_the_coming_season()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var dates = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "DATES", nameEn = "Dates 1 kg", nameAr = "تمر 1 كغ", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 25m
        })).Id;
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new[] { new { itemId = dates, quantity = 20m, unitCost = 15m } }
        })).EnsureSuccessStatusCode();
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C1", nameEn = "Customer", nameAr = "عميل" })).GetProperty("id").GetGuid();
        // 14 sold in the last weeks: 0.25 a day, so 7.5 over 30 days with no history from last year.
        var invoice = await c.PostAsync<JsonElement>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, warehouseId = main, lines = new[] { new { itemId = dates, quantity = 14m } }
        });
        await c.PostAsync<JsonElement>($"/api/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });

        var report = await c.GetAsync<JsonElement>("/api/inventory/reports/seasonal-demand?horizonDays=30");
        var row = report.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("sku").GetString().ShouldBe("DATES");
        row.GetProperty("dailyRecent").GetDecimal().ShouldBe(0.25m);
        row.GetProperty("hasHistory").GetBoolean().ShouldBeFalse();
        row.GetProperty("forecast").GetDecimal().ShouldBe(7.5m);
        row.GetProperty("onHand").GetDecimal().ShouldBe(6m);
        row.GetProperty("suggestedExtra").GetDecimal().ShouldBe(2m);
    }
}

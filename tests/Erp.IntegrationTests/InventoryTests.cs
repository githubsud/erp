using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class InventoryTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Master(Guid Id, string Code);
    public sealed record Item(Guid Id, string Sku, decimal QuantityOnHand, decimal AverageCost, decimal StockValue);
    public sealed record StockRow(Guid ItemId, string Sku, decimal Quantity, decimal AverageCost, decimal Value, bool BelowReorder);
    public sealed record Valuation(decimal TotalValue, decimal LedgerBalance, decimal Difference);
    public sealed record Adjustment(Guid Id, string AdjustmentNo, string Status, decimal TotalValue, Guid? JournalEntryId);
    public sealed record CountLine(Guid ItemId, decimal SystemQuantity, decimal? CountedQuantity);
    public sealed record Count(Guid Id, string Status, Guid? StockAdjustmentId, IReadOnlyList<CountLine> Lines);

    internal static async Task<(Guid Main, Guid Group, Guid Pcs)> DefaultsAsync(HttpClient c)
    {
        var main = (await c.GetAsync<List<Master>>("/api/inventory/warehouses")).Single(w => w.Code == "MAIN").Id;
        var group = (await c.GetAsync<List<Master>>("/api/inventory/item-groups")).Single(g => g.Code == "GEN").Id;
        var pcs = (await c.GetAsync<List<Master>>("/api/inventory/uoms")).Single(u => u.Code == "PCS").Id;
        return (main, group, pcs);
    }

    internal static async Task<Guid> CreateItemAsync(HttpClient c, string sku, string type = "Stock", decimal? reorder = null)
    {
        var (_, group, pcs) = await DefaultsAsync(c);
        var item = await c.PostAsync<Item>("/api/inventory/items", new
        {
            sku, nameEn = $"Item {sku}", nameAr = "صنف", itemType = type, itemGroupId = group, uomId = pcs, reorderLevel = reorder
        });
        return item.Id;
    }

    private static Task<HttpResponseMessage> AdjustAsync(HttpClient c, Guid warehouse, string reason, params object[] lines) =>
        c.PostJsonAsync("/api/inventory/adjustments", new { warehouseId = warehouse, date = Today, reason, lines });

    [Fact]
    public async Task Weighted_average_cost_flows_through_adjustments_transfers_and_counts_and_matches_the_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await DefaultsAsync(c);
        var store = (await c.PostAsync<Master>("/api/inventory/warehouses", new { code = "SHOP", nameEn = "Shop", nameAr = "المعرض" })).Id;
        var item = await CreateItemAsync(c, "PIPE-20", reorder: 5);

        // Opening stock 10 @ 5.00 → Dr Inventory 50 / Cr Opening Balance Equity.
        var opening = await (await AdjustAsync(c, main, "Opening", new { itemId = item, quantity = 10m, unitCost = 5m })).ReadAsync<Adjustment>();
        opening.TotalValue.ShouldBe(50m);
        opening.JournalEntryId.ShouldNotBeNull();

        // Damage 2 at the average (5.00); a gain of 4 @ 8.00 → 12 units worth 72 → average 6.00.
        (await (await AdjustAsync(c, main, "Damage", new { itemId = item, quantity = -2m })).ReadAsync<Adjustment>()).TotalValue.ShouldBe(-10m);
        await (await AdjustAsync(c, main, "Other", new { itemId = item, quantity = 4m, unitCost = 8m })).EnsureSuccessAsync();
        var current = await c.GetAsync<Item>($"/api/inventory/items/{item}");
        current.QuantityOnHand.ShouldBe(12m);
        current.StockValue.ShouldBe(72m);
        current.AverageCost.ShouldBe(6m);

        // Transfer 5 to the shop: quantities move, the company value does not.
        var transfer = await c.PostAsync<JsonElement>("/api/inventory/transfers",
            new { fromWarehouseId = main, toWarehouseId = store, date = Today, lines = new[] { new { itemId = item, quantity = 5m } } });
        transfer.GetProperty("lines")[0].GetProperty("value").GetDecimal().ShouldBe(30m);
        (await c.GetAsync<List<StockRow>>($"/api/inventory/stock?warehouseId={store}")).Single(r => r.ItemId == item).Quantity.ShouldBe(5m);
        (await c.GetAsync<List<StockRow>>($"/api/inventory/stock?warehouseId={main}")).Single(r => r.ItemId == item).Quantity.ShouldBe(7m);

        // The main warehouse cannot issue more than it holds.
        var tooMuch = await AdjustAsync(c, main, "Damage", new { itemId = item, quantity = -8m });
        tooMuch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await tooMuch.ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.insufficient_stock");

        // Count the main warehouse: 6 found against 7 frozen → one adjustment of −1 at 6.00.
        var count = await c.PostAsync<Count>("/api/inventory/counts", new { warehouseId = main, date = Today });
        count.Lines.Single(l => l.ItemId == item).SystemQuantity.ShouldBe(7m);
        (await c.PostJsonAsync($"/api/inventory/counts/{count.Id}/post", new { })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // not counted yet
        await c.PutAsync<Count>($"/api/inventory/counts/{count.Id}/lines", new[] { new { itemId = item, countedQuantity = 6m } });
        count = await c.PostAsync<Count>($"/api/inventory/counts/{count.Id}/post", new { });
        count.Status.ShouldBe("Posted");
        var countAdjustment = await c.GetAsync<Adjustment>($"/api/inventory/adjustments/{count.StockAdjustmentId}");
        countAdjustment.TotalValue.ShouldBe(-6m);

        // Valuation from the stock ledger equals the Inventory account; offsets landed where expected.
        var valuation = await c.GetAsync<Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.TotalValue.ShouldBe(66m);
        valuation.LedgerBalance.ShouldBe(66m);
        valuation.Difference.ShouldBe(0m);
        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.Rows.Single(r => r.Code == "3150").Closing.ShouldBe(-50m);
        tb.Rows.Single(r => r.Code == "5120").Closing.ShouldBe(10m - 32m + 6m);

        // Reversing the opening is refused while the main warehouse holds only 6 of the 10; reversing the transfer brings 5 back.
        (await c.PostJsonAsync($"/api/inventory/adjustments/{opening.Id}/reverse", new { date = Today, reason = "Test" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var reversed = await c.PostAsync<JsonElement>($"/api/inventory/transfers/{transfer.GetProperty("id").GetGuid()}/reverse",
            new { date = Today, reason = "Wrong warehouse" });
        reversed.GetProperty("status").GetString().ShouldBe("Reversed");
        (await c.GetAsync<List<StockRow>>($"/api/inventory/stock?warehouseId={store}")).Single(r => r.ItemId == item).Quantity.ShouldBe(0m);

        var ledger = await c.GetAsync<JsonElement>($"/api/inventory/items/{item}/ledger?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var rows = ledger.GetProperty("rows").EnumerateArray().ToList();
        rows.Last().GetProperty("quantityAfter").GetDecimal().ShouldBe(11m);
        rows.Last().GetProperty("valueAfter").GetDecimal().ShouldBe(66m);
    }

    [Fact]
    public async Task Concurrent_issues_of_the_last_unit_let_exactly_one_succeed()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await DefaultsAsync(c);
        var item = await CreateItemAsync(c, "LAST-1");
        await (await AdjustAsync(c, main, "Opening", new { itemId = item, quantity = 1m, unitCost = 100m })).EnsureSuccessAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AdjustAsync(c, main, "Damage", new { itemId = item, quantity = -1m })));
        attempts.Count(r => r.IsSuccessStatusCode).ShouldBe(1);
        attempts.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(3);
        (await c.GetAsync<Item>($"/api/inventory/items/{item}")).QuantityOnHand.ShouldBe(0m);
    }

    [Fact]
    public async Task Service_items_carry_no_stock()
    {
        var t = await fixture.CreateTenantAsync();
        var (main, _, _) = await DefaultsAsync(t.Client);
        var service = await CreateItemAsync(t.Client, "FREIGHT", "Service");
        var response = await AdjustAsync(t.Client, main, "Other", new { itemId = service, quantity = 1m, unitCost = 10m });
        (await response.ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.not_stock_item");
    }
}

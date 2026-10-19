using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class KitAndReorderTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private static async Task<Guid> ItemAsync(HttpClient c, string sku, Guid group, Guid uom, decimal? reorderLevel = null, decimal? reorderQuantity = null) =>
        (await c.PostAsync<JsonElement>("/api/inventory/items", new
        {
            sku, nameEn = $"Item {sku}", nameAr = "صنف", itemType = "Stock", itemGroupId = group, uomId = uom, reorderLevel, reorderQuantity
        })).GetProperty("id").GetGuid();

    [Fact]
    public async Task Assembling_kits_moves_component_value_into_the_kit_and_reversing_takes_it_apart()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, gen, pcs) = await InventoryTests.DefaultsAsync(c);
        var hampers = (await c.PostAsync<InventoryTests.Master>("/api/inventory/item-groups", new { code = "HAMP", nameEn = "Hampers", nameAr = "سلال" })).Id;
        var dates = await ItemAsync(c, "DATES", gen, pcs);
        var coffee = await ItemAsync(c, "COFFEE", gen, pcs);
        var hamper = await ItemAsync(c, "HAMPER", hampers, pcs);
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening",
            lines = new object[] { new { itemId = dates, quantity = 20m, unitCost = 15m }, new { itemId = coffee, quantity = 10m, unitCost = 40m } }
        })).EnsureSuccessStatusCode();

        (await (await c.PutJsonAsync($"/api/inventory/items/{dates}/components", new[] { new { itemId = dates, quantity = 1m } })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("inv.kit_components");
        var components = await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{hamper}/components", new[]
        {
            new { itemId = dates, quantity = 2m }, new { itemId = coffee, quantity = 1m }
        });
        components.Count.ShouldBe(2);

        // 5 hampers = 10 dates × 15 + 5 coffee × 40 = 350.
        var asm = await c.PostAsync<JsonElement>("/api/inventory/assemblies", new { kitItemId = hamper, warehouseId = main, date = Today, quantity = 5m });
        asm.GetProperty("totalValue").GetDecimal().ShouldBe(350m);
        var stock = await c.GetAsync<List<JsonElement>>("/api/inventory/stock");
        decimal Qty(Guid id) => stock.Single(s => s.GetProperty("itemId").GetGuid() == id).GetProperty("quantity").GetDecimal();
        Qty(hamper).ShouldBe(5m);
        Qty(dates).ShouldBe(10m);
        Qty(coffee).ShouldBe(5m);
        (await (await c.PostJsonAsync("/api/inventory/assemblies", new { kitItemId = hamper, warehouseId = main, date = Today, quantity = 6m }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.insufficient_stock");
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.Difference.ShouldBe(0m);
        valuation.TotalValue.ShouldBe(700m);

        await c.PostAsync<JsonElement>($"/api/inventory/assemblies/{asm.GetProperty("id").GetGuid()}/reverse", new { date = Today, reason = "Unpacked" });
        stock = await c.GetAsync<List<JsonElement>>("/api/inventory/stock");
        Qty(hamper).ShouldBe(0m);
        Qty(dates).ShouldBe(20m);
        (await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}")).Difference.ShouldBe(0m);
    }

    [Fact]
    public async Task Reorder_suggestions_use_the_last_supplier_and_price_and_become_draft_orders()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, gen, pcs) = await InventoryTests.DefaultsAsync(c);
        var gloves = await ItemAsync(c, "GLOVES", gen, pcs, reorderLevel: 10m, reorderQuantity: 50m);
        var masks = await ItemAsync(c, "MASKS", gen, pcs, reorderLevel: 5m);
        var supplier = await PurchasingTests.SupplierAsync(c, "S-SAFE");
        var order = await PurchasingTests.OrderAsync(c, supplier, main, (gloves, 4m, 7m));
        (await c.PostJsonAsync("/api/purchasing/receipts", new
        {
            purchaseOrderId = order.Id, receiptDate = Today, lines = new[] { new { purchaseOrderLineId = order.Lines[0].Id, quantity = 4m } }
        })).EnsureSuccessStatusCode();

        var suggestions = await c.GetAsync<List<JsonElement>>("/api/purchasing/reorder-suggestions");
        var g = suggestions.Single(s => s.GetProperty("itemId").GetGuid() == gloves);
        g.GetProperty("onHand").GetDecimal().ShouldBe(4m);
        g.GetProperty("suggestedQuantity").GetDecimal().ShouldBe(50m);
        g.GetProperty("supplierId").GetGuid().ShouldBe(supplier);
        g.GetProperty("unitPrice").GetDecimal().ShouldBe(7m);
        var m = suggestions.Single(s => s.GetProperty("itemId").GetGuid() == masks);
        m.GetProperty("suggestedQuantity").GetDecimal().ShouldBe(10m);     // back to twice the level
        m.GetProperty("supplierId").ValueKind.ShouldBe(JsonValueKind.Null);

        var created = await c.PostAsync<JsonElement>("/api/purchasing/reorder-suggestions/orders", new
        {
            warehouseId = main, orderDate = Today, lines = new[] { new { itemId = gloves, supplierId = supplier, quantity = 50m, unitPrice = 7m } }
        });
        var po = await c.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{created.GetProperty("purchaseOrderIds")[0].GetGuid()}");
        po.Status.ShouldBe("Draft");
        po.Total.ShouldBe(350m);
        // Now on order: no longer suggested.
        (await c.GetAsync<List<JsonElement>>("/api/purchasing/reorder-suggestions")).ShouldNotContain(s => s.GetProperty("itemId").GetGuid() == gloves);
    }
}

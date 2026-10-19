using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SourcingTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    [Fact]
    public async Task Rfq_quotes_are_compared_and_awarded_per_line_into_draft_orders()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var cement = await InventoryTests.CreateItemAsync(c, "CEM");
        var steel = await InventoryTests.CreateItemAsync(c, "STL");
        var a = await PurchasingTests.SupplierAsync(c, "S-A");
        var b = await PurchasingTests.SupplierAsync(c, "S-B");
        var x = await PurchasingTests.SupplierAsync(c, "S-X");

        var rfq = await c.PostAsync<JsonElement>("/api/purchasing/rfqs", new
        {
            rfqDate = Today, replyBy = Today.AddDays(7), warehouseId = main, supplierIds = new[] { a, b },
            lines = new[] { new { itemId = cement, quantity = 100m }, new { itemId = steel, quantity = 10m } }
        });
        var id = rfq.GetProperty("id").GetGuid();
        var lines = rfq.GetProperty("lines").EnumerateArray().ToList();
        Guid Line(int i) => lines[i].GetProperty("rfqLineId").GetGuid();

        // A: cement 20, steel 300 (= 2,000 + 3,000); B: cement 18, steel 320 (= 1,800 + 3,200). Best mix = 1,800 + 3,000.
        await c.PutAsync<JsonElement>($"/api/purchasing/rfqs/{id}/quotes", new
        {
            supplierId = a, quotes = new object[] { new { rfqLineId = Line(0), unitPrice = 20m, leadTimeDays = 5 }, new { rfqLineId = Line(1), unitPrice = 300m } }
        });
        var cmp = await c.PutAsync<JsonElement>($"/api/purchasing/rfqs/{id}/quotes", new
        {
            supplierId = b, quotes = new object[] { new { rfqLineId = Line(0), unitPrice = 18m }, new { rfqLineId = Line(1), unitPrice = 320m } }
        });
        (await (await c.PutJsonAsync($"/api/purchasing/rfqs/{id}/quotes", new { supplierId = x, quotes = Array.Empty<object>() })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("pur.rfq_not_invited");
        var totals = cmp.GetProperty("suppliers").EnumerateArray().ToDictionary(s => s.GetProperty("supplierId").GetGuid(), s => s.GetProperty("total").GetDecimal());
        totals[a].ShouldBe(5_000m);
        totals[b].ShouldBe(5_000m);
        cmp.GetProperty("bestMixTotal").GetDecimal().ShouldBe(4_800m);
        cmp.GetProperty("lines")[0].GetProperty("cells").EnumerateArray().Single(cell => cell.GetProperty("lowest").GetBoolean())
            .GetProperty("supplierId").GetGuid().ShouldBe(b);

        var award = await c.PostAsync<JsonElement>($"/api/purchasing/rfqs/{id}/award", new
        {
            lines = new Dictionary<Guid, Guid> { [Line(0)] = b, [Line(1)] = a }, orderDate = Today
        });
        var orders = award.GetProperty("purchaseOrderIds").EnumerateArray().Select(o => o.GetGuid()).ToList();
        orders.Count.ShouldBe(2);
        var totalsByOrder = new List<decimal>();
        foreach (var o in orders)
        {
            var order = await c.GetAsync<PurchasingTests.Order>($"/api/purchasing/orders/{o}");
            order.Status.ShouldBe("Draft");
            totalsByOrder.Add(order.Total);
        }
        totalsByOrder.ShouldBe([1_800m, 3_000m], ignoreOrder: true);
        (await c.GetAsync<JsonElement>($"/api/purchasing/rfqs/{id}")).GetProperty("status").GetString().ShouldBe("Awarded");
    }

    [Fact]
    public async Task Blanket_agreement_releases_orders_at_agreed_prices_within_its_quantity()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var diesel = await InventoryTests.CreateItemAsync(c, "DSL");
        var supplier = await PurchasingTests.SupplierAsync(c, "S-FUEL");

        var agreement = await c.PostAsync<JsonElement>("/api/purchasing/blanket-agreements", new
        {
            supplierId = supplier, validFrom = Today.AddDays(-1), validTo = Today.AddDays(90),
            lines = new[] { new { itemId = diesel, unitPrice = 2.05m, maxQuantity = 10_000m } }
        });
        var id = agreement.GetProperty("id").GetGuid();
        var first = await c.PostAsync<JsonElement>($"/api/purchasing/blanket-agreements/{id}/release", new
        {
            orderDate = Today, warehouseId = main, lines = new[] { new { itemId = diesel, quantity = 6_000m } }
        });
        first.GetProperty("total").GetDecimal().ShouldBe(12_300m);
        (await (await c.PostJsonAsync($"/api/purchasing/blanket-agreements/{id}/release", new
        {
            orderDate = Today, warehouseId = main, lines = new[] { new { itemId = diesel, quantity = 5_000m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.blanket_exceeded");
        (await (await c.PostJsonAsync($"/api/purchasing/blanket-agreements/{id}/release", new
        {
            orderDate = Today.AddDays(120), warehouseId = main, lines = new[] { new { itemId = diesel, quantity = 1m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.blanket_expired");

        // Released orders keep the agreed price; cancelling one gives its quantity back.
        var orderId = first.GetProperty("purchaseOrderId").GetGuid();
        (await (await c.PutJsonAsync($"/api/purchasing/orders/{orderId}", new
        {
            supplierId = supplier, orderDate = Today, warehouseId = main, lines = new[] { new { itemId = diesel, quantity = 6_000m, unitPrice = 3m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.po_blanket");
        var detail = await c.GetAsync<JsonElement>($"/api/purchasing/blanket-agreements/{id}");
        detail.GetProperty("lines")[0].GetProperty("remaining").GetDecimal().ShouldBe(4_000m);
        await c.PostAsync<JsonElement>($"/api/purchasing/orders/{orderId}/cancel", new { });
        detail = await c.GetAsync<JsonElement>($"/api/purchasing/blanket-agreements/{id}");
        detail.GetProperty("lines")[0].GetProperty("remaining").GetDecimal().ShouldBe(10_000m);
        detail.GetProperty("releases").GetArrayLength().ShouldBe(1);
    }
}

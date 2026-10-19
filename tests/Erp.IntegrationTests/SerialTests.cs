using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SerialTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    [Fact]
    public async Task Serial_numbered_units_are_received_moved_and_traced_one_by_one()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var shop = (await c.PostAsync<InventoryTests.Master>("/api/inventory/warehouses", new { code = "SHOP", nameEn = "Shop", nameAr = "المعرض" })).Id;
        var phone = (await c.PostAsync<JsonElement>("/api/inventory/items", new
        {
            sku = "PH-1", nameEn = "Phone", nameAr = "هاتف", itemType = "Stock", itemGroupId = group, uomId = pcs, trackSerials = true
        })).GetProperty("id").GetGuid();
        Task<HttpResponseMessage> Adjust(Guid warehouse, string reason, decimal quantity, string[]? serials, decimal? unitCost = 1_000m) =>
            c.PostJsonAsync("/api/inventory/adjustments", new
            {
                warehouseId = warehouse, date = Today, reason, lines = new[] { new { itemId = phone, quantity, unitCost, serials } }
            });

        // Receipts need one serial per unit, whole units only, and no serial twice.
        (await (await Adjust(main, "Opening", 3m, ["SN-A", "SN-B"])).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.serials_required");
        (await (await Adjust(main, "Opening", 1.5m, ["SN-A"])).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.serial_whole");
        (await (await Adjust(main, "Opening", 2m, ["SN-A", "sn-a"])).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.serial_values");
        (await Adjust(main, "Opening", 3m, ["SN-A", "SN-B", "SN-C"])).EnsureSuccessStatusCode();
        (await (await Adjust(main, "Opening", 1m, ["SN-B"])).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.serial_in_stock");

        // Move SN-B to the shop; write off a named unit there; serials on hand per warehouse.
        (await c.PostJsonAsync("/api/inventory/transfers", new
        {
            fromWarehouseId = main, toWarehouseId = shop, date = Today, lines = new[] { new { itemId = phone, quantity = 1m } }
        })).EnsureSuccessStatusCode();
        var atShop = await c.GetAsync<List<JsonElement>>($"/api/inventory/items/{phone}/serials?warehouseId={shop}");
        atShop.Single().GetProperty("serialNo").GetString().ShouldBe("SN-A");   // issued in serial order
        (await (await Adjust(shop, "Damage", -1m, ["SN-C"], null)).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.insufficient_stock");
        (await Adjust(shop, "Damage", -1m, ["SN-A"], null)).EnsureSuccessStatusCode();
        (await c.GetAsync<List<JsonElement>>($"/api/inventory/items/{phone}/serials")).Select(s => s.GetProperty("serialNo").GetString())
            .ShouldBe(["SN-B", "SN-C"]);

        var trace = (await c.GetAsync<List<JsonElement>>("/api/inventory/serials/sn-a")).Single();
        trace.GetProperty("inStock").GetBoolean().ShouldBeFalse();
        trace.GetProperty("history").GetArrayLength().ShouldBe(4); // in at MAIN, out of MAIN, in at SHOP, written off at SHOP
        (await c.GetAsync<List<JsonElement>>("/api/inventory/serials/SN-B")).Single().GetProperty("warehouseCode").GetString().ShouldBe("MAIN");

        // A serial that left can come back (customer return).
        (await Adjust(main, "Other", 1m, ["SN-A"])).EnsureSuccessStatusCode();

        // Bin locations.
        var bins = await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{phone}/bins", new[] { new { warehouseId = main, binCode = "a-01-3" } });
        bins.Single().GetProperty("binCode").GetString().ShouldBe("A-01-3");
        (await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{phone}/bins", new[] { new { warehouseId = main, binCode = (string?)null } })).ShouldBeEmpty();
    }
}

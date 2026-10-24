using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>
/// Spare parts: a brand on the item, other part numbers (OEM and replacements) and the cars a part fits. The item search finds a
/// part by any of its numbers however it is typed, by brand, and by car and model year ("corolla 2015").
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class SparePartsTests(ErpFixture fixture)
{
    private static async Task<List<string>> SearchAsync(HttpClient c, string term) =>
        (await c.GetAsync<JsonElement>($"/api/inventory/items?search={Uri.EscapeDataString(term)}")).GetProperty("items").EnumerateArray()
        .Select(i => i.GetProperty("sku").GetString()!).Order().ToList();

    [Fact]
    public async Task A_part_is_found_by_any_number_its_brand_and_the_cars_it_fits()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (_, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var filter = (await c.PostAsync<JsonElement>("/api/inventory/items", new
        {
            sku = "OF-DENSO-1", nameEn = "Oil filter", nameAr = "فلتر زيت", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 25m, brand = "Denso"
        }));
        filter.GetProperty("brand").GetString().ShouldBe("Denso");
        var filterId = filter.GetProperty("id").GetGuid();
        var plug = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "PLUG-NGK", nameEn = "Spark plug", nameAr = "بوجي", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 18m, brand = "NGK"
        })).Id;

        var parts = await c.PutAsync<JsonElement>($"/api/inventory/items/{filterId}/parts", new
        {
            partNumbers = new[] { new { partNo = "04152-YZZA1", brand = "Toyota" }, new { partNo = "90915-YZZE1", brand = "" } },
            fitments = new object[]
            {
                new { make = "Toyota", model = "Corolla", yearFrom = 2014, yearTo = 2019, engine = "1.6" },
                new { make = "Toyota", model = "Camry", yearFrom = 2012, yearTo = (int?)null }
            }
        });
        parts.GetProperty("partNumbers").GetArrayLength().ShouldBe(2);
        parts.GetProperty("fitments").GetArrayLength().ShouldBe(2);
        await c.PutAsync<JsonElement>($"/api/inventory/items/{plug}/parts", new
        {
            partNumbers = Array.Empty<object>(), fitments = new[] { new { make = "Nissan", model = "Patrol", yearFrom = 2010, yearTo = 2016 } }
        });

        // Any spelling of the OEM number, the brand, and car plus model year (open-ended years fit every later model).
        (await SearchAsync(c, "04152yzza1")).ShouldBe(["OF-DENSO-1"]);
        (await SearchAsync(c, "04152 YZZA1")).ShouldBe(["OF-DENSO-1"]);
        (await SearchAsync(c, "denso")).ShouldBe(["OF-DENSO-1"]);
        (await SearchAsync(c, "corolla 2015")).ShouldBe(["OF-DENSO-1"]);
        (await SearchAsync(c, "corolla 2021")).ShouldBeEmpty();
        (await SearchAsync(c, "camry 2025 filter")).ShouldBe(["OF-DENSO-1"]);
        (await SearchAsync(c, "patrol 2012")).ShouldBe(["PLUG-NGK"]);

        // The till's catalogue carries them too, so its offline search finds parts the same way.
        var catalog = (await c.GetAsync<List<JsonElement>>("/api/sales/pos/catalog")).Single(i => i.GetProperty("id").GetGuid() == filterId);
        catalog.GetProperty("brand").GetString().ShouldBe("Denso");
        catalog.GetProperty("partKeys").EnumerateArray().Select(k => k.GetString()).ShouldContain("04152YZZA1");
        catalog.GetProperty("fits").GetArrayLength().ShouldBe(2);

        // The same number twice, a car without a model and backwards years are refused.
        (await (await c.PutJsonAsync($"/api/inventory/items/{filterId}/parts", new
        {
            partNumbers = new[] { new { partNo = "04152-YZZA1" }, new { partNo = "04152yzza1" } }, fitments = Array.Empty<object>()
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.part_no_twice");
        (await (await c.PutJsonAsync($"/api/inventory/items/{filterId}/parts", new
        {
            partNumbers = Array.Empty<object>(), fitments = new[] { new { make = "Toyota", model = "" } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.fitment_car");
        (await (await c.PutJsonAsync($"/api/inventory/items/{filterId}/parts", new
        {
            partNumbers = Array.Empty<object>(), fitments = new[] { new { make = "Toyota", model = "Yaris", yearFrom = 2020, yearTo = 2015 } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.fitment_years");
    }
}

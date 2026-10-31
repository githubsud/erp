using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>
/// The public storefront: anonymous shoppers reach an open store by slug or custom domain and see only the whitelist (store profile,
/// published listings, guest prices, availability bands) — never cost, supplier, exact stock or internal ids.
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class CommerceStorefrontTests(ErpFixture fixture)
{
    private static readonly string[] ProductFields =
        ["slug", "titleEn", "titleAr", "categorySlug", "price", "uomCode", "units", "availability", "images", "brand", "fits", "descriptionEn", "descriptionAr"];

    private static async Task<Guid> ItemAsync(HttpClient c, string sku, decimal price, Guid group, Guid pcs, string type = "Stock", decimal? reorderLevel = null) =>
        (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku, nameEn = $"Item {sku}", nameAr = $"صنف {sku}", itemType = type, itemGroupId = group, uomId = pcs, salesPrice = price, reorderLevel,
            purchasePrice = 3m
        })).Id;

    [Fact]
    public async Task Shoppers_see_published_items_at_till_prices_with_availability_bands_only()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var spares = (await c.PostAsync<InventoryTests.Master>("/api/inventory/item-groups", new { code = "SPARE", nameEn = "Spares", nameAr = "قطع غيار" })).Id;
        var box = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "BOX12", nameEn = "Box of 12", nameAr = "علبة 12" })).Id;
        var plug = await ItemAsync(c, "PLUG", 10.5m, spares, pcs, reorderLevel: 20m);
        await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{plug}/units", new object[] { new { uomId = box, factor = 12m, salesPrice = 100m } });
        var filter = await ItemAsync(c, "FILTER", 25m, group, pcs, reorderLevel: 10m);
        var belt = await ItemAsync(c, "BELT", 70m, group, pcs);
        var fitting = await ItemAsync(c, "FITTING", 40m, group, pcs, "Service");
        var hidden = await ItemAsync(c, "HIDDEN", 5m, group, pcs);
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = FinanceTests.Today, reason = "Opening",
            lines = new[] { new { itemId = plug, quantity = 100m, unitCost = 6m }, new { itemId = filter, quantity = 5m, unitCost = 12m } }
        })).EnsureSuccessStatusCode();

        var slug = t.Code + "-shop";
        await c.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(slug, host: $"{t.Code}.shop.qa"));
        await c.PostAsync<JsonElement>("/api/commerce/categories", new { slug = "spares", nameEn = "Spares", nameAr = "قطع غيار", itemGroupId = spares });
        var services = await c.PostAsync<JsonElement>("/api/commerce/categories", new { slug = "services", nameEn = "Services", nameAr = "خدمات" });
        await c.PostAsync<JsonElement>("/api/commerce/listings/publish", new { itemIds = new[] { plug, filter, belt } });
        await c.PostAsync<JsonElement>("/api/commerce/listings", new
        {
            itemId = fitting, slug = "fitting", descriptionEn = "We fit it", descriptionAr = "نركبها لك", categoryId = services.GetProperty("id").GetGuid(), isPublished = true
        });
        await c.PostAsync<JsonElement>("/api/commerce/listings", new { itemId = hidden, isPublished = false });

        var shopper = fixture.Factory.CreateClient();
        var profile = await shopper.GetAsync<JsonElement>($"/api/store/{slug}");
        profile.GetProperty("nameEn").GetString().ShouldBe($"Store {slug}");
        profile.GetProperty("currency").GetString().ShouldBe("QAR");
        profile.GetProperty("deliveryFee").GetDecimal().ShouldBe(15m);
        profile.EnumerateObject().Select(p => p.Name).ShouldNotContain("id");

        // Published only, priced like the till charges a walk-in customer, with bands instead of quantities.
        var page = await shopper.GetAsync<JsonElement>($"/api/store/{slug}/catalog");
        page.GetProperty("total").GetInt32().ShouldBe(4);
        var products = page.GetProperty("items").EnumerateArray().ToDictionary(p => p.GetProperty("slug").GetString()!);
        products.Keys.ShouldBe(["belt", "filter", "fitting", "plug"], ignoreOrder: true);
        var till = (await c.GetAsync<List<JsonElement>>("/api/sales/pos/catalog")).ToDictionary(i => i.GetProperty("id").GetGuid());
        products["plug"].GetProperty("price").GetDecimal().ShouldBe(till[plug].GetProperty("salesPrice").GetDecimal());
        products["plug"].GetProperty("units").EnumerateArray().Select(u => (u.GetProperty("uomCode").GetString(), u.GetProperty("price").GetDecimal()))
            .ShouldBe([("PCS", 10.5m), ("BOX12", 100m)]);
        products["plug"].GetProperty("availability").GetString().ShouldBe("InStock");
        products["filter"].GetProperty("availability").GetString().ShouldBe("Low");
        products["belt"].GetProperty("availability").GetString().ShouldBe("OutOfStock");
        products["fitting"].GetProperty("availability").GetString().ShouldBe("InStock");
        products["plug"].GetProperty("categorySlug").GetString().ShouldBe("spares");

        // The whitelist: nothing else, so no cost, purchase price, quantity, SKU or ids.
        foreach (var product in products.Values)
            product.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(ProductFields);
        var raw = await shopper.GetStringAsync($"/api/store/{slug}/catalog");
        foreach (var secret in new[] { plug.ToString(), t.TenantId.ToString(), t.CompanyId.ToString(), "\"onHand\"", "cost", "purchase", "\"sku\"" })
            raw.ShouldNotContain(secret, Case.Insensitive);

        // Categories: explicit, or by inventory group; search; paging.
        (await shopper.GetAsync<JsonElement>($"/api/store/{slug}/catalog?category=spares")).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString()).ShouldBe(["plug"]);
        (await shopper.GetAsync<JsonElement>($"/api/store/{slug}/catalog?category=services")).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString()).ShouldBe(["fitting"]);
        (await shopper.GetAsync<JsonElement>($"/api/store/{slug}/catalog?category=nope")).GetProperty("total").GetInt32().ShouldBe(0);
        (await shopper.GetAsync<JsonElement>($"/api/store/{slug}/catalog?search=filt")).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString()).ShouldBe(["filter"]);
        var second = await shopper.GetAsync<JsonElement>($"/api/store/{slug}/catalog?page=2&pageSize=3");
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await shopper.GetAsync<List<JsonElement>>($"/api/store/{slug}/categories")).Select(x => x.GetProperty("slug").GetString())
            .ShouldBe(["services", "spares"], ignoreOrder: true);

        var fittingPage = await shopper.GetAsync<JsonElement>($"/api/store/{slug}/products/fitting");
        fittingPage.GetProperty("descriptionAr").GetString().ShouldBe("نركبها لك");
        (await shopper.GetAsync($"/api/store/{slug}/products/hidden")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.GetAsync($"/api/store/{slug}/products/nothing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A custom domain reaches the same store through the reserved "_" slug; an unknown host does not.
        var byHost = new HttpRequestMessage(HttpMethod.Get, "/api/store/_");
        byHost.Headers.Host = $"{t.Code}.shop.qa";
        (await (await shopper.SendAsync(byHost)).ReadAsync<JsonElement>()).GetProperty("slug").GetString().ShouldBe(slug);
        var unknownHost = new HttpRequestMessage(HttpMethod.Get, "/api/store/_/catalog");
        unknownHost.Headers.Host = "nobody.shop.qa";
        (await shopper.SendAsync(unknownHost)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Draft or closed stores, and companies without the module, are not there for shoppers.
        await c.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(slug, "Closed"));
        (await shopper.GetAsync($"/api/store/{slug}/catalog")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await c.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(slug, "Draft"));
        (await shopper.GetAsync($"/api/store/{slug}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await c.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(slug));
        (await shopper.GetAsync($"/api/store/{slug}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await c.PutAsync<JsonElement>("/api/core/modules", new { disabled = new[] { "commerce" } });
        (await shopper.GetAsync($"/api/store/{slug}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.GetAsync("/api/store/no-such-store/catalog")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Shoppers_find_spare_parts_by_part_number_brand_or_car_like_at_the_till()
    {
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        var c = s.Tenant.Client;
        var filter = await CommerceAdminTests.ItemAsync(c, "OF-1", 35m);
        await c.PutAsync<JsonElement>($"/api/inventory/items/{filter}/parts", new
        {
            partNumbers = new[] { new { partNo = "04152-YZZA1", brand = "Toyota" } },
            fitments = new object[]
            {
                new { make = "Toyota", model = "Corolla", yearFrom = 2014, yearTo = 2019, engine = "1.6" },
                new { make = "Toyota", model = "Camry", yearFrom = 2021, yearTo = (int?)null }
            }
        });
        await c.PostAsync<JsonElement>("/api/commerce/listings", new { itemId = filter, slug = "oil-filter", titleEn = "Oil filter", titleAr = "فلتر زيت", isPublished = true });
        var shopper = fixture.Factory.CreateClient();
        async Task<string[]> Find(string term) =>
            (await shopper.GetAsync<JsonElement>($"/api/store/{s.Slug}/catalog?search={Uri.EscapeDataString(term)}")).GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString()!).ToArray();

        (await Find("04152yzza1")).ShouldBe(["oil-filter"]);
        (await Find("04152 YZZA1")).ShouldBe(["oil-filter"]);
        (await Find("فلتر")).ShouldBe(["oil-filter"]);
        (await Find("corolla 2015")).ShouldBe(["oil-filter"]);
        (await Find("corolla 2015 filter")).ShouldBe(["oil-filter"]);
        // A model year belongs to one car: the 2021 Camry fit does not make it a 2021 Corolla part.
        (await Find("corolla 2021")).ShouldBeEmpty();
        (await Find("camry 2024")).ShouldBe(["oil-filter"]);
        (await Find("patrol")).ShouldBeEmpty();

        // The product page says which cars it fits.
        var product = await shopper.GetAsync<JsonElement>($"/api/store/{s.Slug}/products/oil-filter");
        product.GetProperty("fits").EnumerateArray().Select(f => f.GetProperty("model").GetString()).ShouldBe(["Corolla", "Camry"], ignoreOrder: true);
    }
}

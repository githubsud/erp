using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>The merchant side of the online store: store settings, categories and publishing listings over inventory items.</summary>
[Collection(ErpCollection.Name)]
public sealed class CommerceAdminTests(ErpFixture fixture)
{
    internal static async Task<Guid> ItemAsync(HttpClient c, string sku, decimal price, Guid? group = null)
    {
        var (_, defaultGroup, pcs) = await InventoryTests.DefaultsAsync(c);
        return (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku, nameEn = $"Item {sku}", nameAr = $"صنف {sku}", itemType = "Stock", itemGroupId = group ?? defaultGroup, uomId = pcs, salesPrice = price
        })).Id;
    }

    internal static object Store(string slug, string status = "Open", string? host = null) => new
    {
        slug, nameEn = $"Store {slug}", nameAr = $"متجر {slug}", phone = "+974 5555 0000", host, codEnabled = true, minOrder = 50m, deliveryFee = 15m, status
    };

    [Fact]
    public async Task A_company_starts_with_a_draft_store_and_publishes_its_items()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;

        // Provisioning made a draft store named after the company, at the tenant code.
        var draft = await c.GetAsync<JsonElement>("/api/commerce/store");
        draft.GetProperty("slug").GetString().ShouldBe(t.Code);
        draft.GetProperty("status").GetString().ShouldBe("Draft");
        draft.GetProperty("currency").GetString().ShouldBe("QAR");

        // Addresses are checked; another tenant's store address is taken.
        (await c.PutJsonAsync("/api/commerce/store", Store("Bad Slug!"))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.PutJsonAsync("/api/commerce/store", Store("_"))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.PutJsonAsync("/api/commerce/store", Store(t.Code + "-shop", host: "not a host"))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var other = await fixture.CreateTenantAsync();
        var conflict = await (await c.PutJsonAsync("/api/commerce/store", Store(other.Code))).ProblemAsync();
        conflict.GetProperty("code").GetString().ShouldBe("commerce.slug_taken");

        var slug = t.Code + "-shop";
        var store = await c.PutAsync<JsonElement>("/api/commerce/store", Store(slug, host: $"{t.Code}.shop.qa"));
        store.GetProperty("slug").GetString().ShouldBe(slug);
        store.GetProperty("host").GetString().ShouldBe($"{t.Code}.shop.qa");
        store.GetProperty("status").GetString().ShouldBe("Open");
        store.GetProperty("deliveryFee").GetDecimal().ShouldBe(15m);
        // The old address is free again.
        (await other.Client.PutJsonAsync("/api/commerce/store", Store(t.Code, "Draft"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.Client.PutJsonAsync("/api/commerce/store", Store(other.Code + "-x", host: $"{t.Code}.shop.qa"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Categories: unique address per store, optionally mapped to an inventory group of this company only.
        var (_, group, _) = await InventoryTests.DefaultsAsync(c);
        var (_, otherGroup, _) = await InventoryTests.DefaultsAsync(other.Client);
        var category = await c.PostAsync<JsonElement>("/api/commerce/categories", new { slug = "", nameEn = "Engine Parts", nameAr = "قطع المحرك", itemGroupId = group });
        category.GetProperty("slug").GetString().ShouldBe("engine-parts");
        (await c.PostJsonAsync("/api/commerce/categories", new { slug = "engine-parts", nameEn = "Again", nameAr = "مرة أخرى" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await c.PostJsonAsync("/api/commerce/categories", new { slug = "x-group", nameEn = "X", nameAr = "س", itemGroupId = otherGroup }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A listing defaults to the item's SKU and names; an item is listed once; another company's item does not exist here.
        var plug = await ItemAsync(c, "NGK-BKR6E", 10m);
        var listing = await c.PostAsync<JsonElement>("/api/commerce/listings", new { itemId = plug, isPublished = true });
        listing.GetProperty("slug").GetString().ShouldBe("ngk-bkr6e");
        listing.GetProperty("titleEn").GetString().ShouldBe("Item NGK-BKR6E");
        listing.GetProperty("titleAr").GetString().ShouldBe("صنف NGK-BKR6E");
        (await c.PostJsonAsync("/api/commerce/listings", new { itemId = plug, isPublished = true })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var foreign = await ItemAsync(other.Client, "FOREIGN", 5m);
        (await c.PostJsonAsync("/api/commerce/listings", new { itemId = foreign, isPublished = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.PostJsonAsync("/api/commerce/listings/publish", new { itemIds = new[] { foreign } })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var updated = await c.PutAsync<JsonElement>($"/api/commerce/listings/{listing.GetProperty("id").GetGuid()}", new
        {
            itemId = plug, slug = "spark-plug", titleEn = "Spark plug", titleAr = "بوجي", descriptionEn = "Fits most engines", categoryId = category.GetProperty("id").GetGuid(),
            isPublished = true, sortOrder = 1
        });
        updated.GetProperty("slug").GetString().ShouldBe("spark-plug");
        updated.GetProperty("categoryId").GetGuid().ShouldBe(category.GetProperty("id").GetGuid());

        // Bulk publishing lists new items under their SKU (numbered when the address is taken) and hides others.
        var belt = await ItemAsync(c, "BELT", 70m);
        var plug2 = await ItemAsync(c, "Spark Plug", 12m);
        (await c.PostAsync<JsonElement>("/api/commerce/listings/publish", new { itemIds = new[] { belt, plug2, plug } })).GetProperty("count").GetInt32().ShouldBe(3);
        var listings = await c.GetAsync<List<JsonElement>>("/api/commerce/listings");
        listings.Select(l => l.GetProperty("slug").GetString()).ShouldBe(["belt", "spark-plug", "spark-plug-2"], ignoreOrder: true);
        listings.ShouldAllBe(l => l.GetProperty("isPublished").GetBoolean());
        await c.PostAsync<JsonElement>("/api/commerce/listings/publish", new { itemIds = new[] { belt }, publish = false });
        (await c.GetAsync<List<JsonElement>>("/api/commerce/listings")).Single(l => l.GetProperty("itemId").GetGuid() == belt)
            .GetProperty("isPublished").GetBoolean().ShouldBeFalse();
        // The other company sees none of it.
        (await other.Client.GetAsync<List<JsonElement>>("/api/commerce/listings")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_settings_need_their_permissions()
    {
        var t = await fixture.CreateTenantAsync();
        async Task<HttpClient> UserAsync(string role)
        {
            await t.Client.PostAsync<JsonElement>("/api/core/users", new
            {
                email = $"{role}@{t.Code}.qa", displayNameEn = role, password = "User-Password-123", preferredLanguage = "en", roles = new[] { role }
            });
            return await fixture.LoginAsync(t.Code, $"{role}@{t.Code}.qa", "User-Password-123");
        }
        var cashier = await UserAsync("salesperson");
        var storekeeper = await UserAsync("storekeeper");
        var manager = await UserAsync("general_manager");

        (await cashier.GetAsync("/api/commerce/store")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/commerce/listings")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await storekeeper.PutJsonAsync("/api/commerce/store", Store(t.Code + "-s"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await storekeeper.GetAsync("/api/commerce/listings")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await manager.PutJsonAsync("/api/commerce/store", Store(t.Code + "-m"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Without the module (switched off for the company) the admin API answers 403 like any other module.
        await t.Client.PutAsync<JsonElement>("/api/core/modules", new { disabled = new[] { "commerce" } });
        (await t.Client.GetAsync("/api/commerce/store")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

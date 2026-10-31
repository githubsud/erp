using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>Anonymous carts: a key the browser keeps, lines by product address and unit, re-priced as a guest on every read.</summary>
[Collection(ErpCollection.Name)]
public sealed class CommerceCartTests(ErpFixture fixture)
{
    internal sealed record ReadyStore(TenantSession Tenant, string Slug, Guid Main, Guid Plug, Guid Box, Guid DeliveryItem);

    /// <summary>An open store with a dispatch warehouse and delivery item; PLUG at 10.5 a piece or 100 a box of 12, 10 pieces in stock.</summary>
    internal static async Task<ReadyStore> ReadyStoreAsync(ErpFixture fixture, decimal stock = 10m)
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var box = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "BOX12", nameEn = "Box of 12", nameAr = "علبة 12" })).Id;
        var plug = await CommerceAdminTests.ItemAsync(c, "PLUG", 10.5m);
        await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{plug}/units", new object[] { new { uomId = box, factor = 12m, salesPrice = 100m } });
        var delivery = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "DELIVERY", nameEn = "Delivery", nameAr = "توصيل", itemType = "Service", itemGroupId = group, uomId = pcs, salesPrice = 0m
        })).Id;
        if (stock > 0)
            (await c.PostJsonAsync("/api/inventory/adjustments", new
            {
                warehouseId = main, date = FinanceTests.Today, reason = "Opening", lines = new[] { new { itemId = plug, quantity = stock, unitCost = 6m } }
            })).EnsureSuccessStatusCode();
        var slug = t.Code + "-shop";
        await c.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(slug, warehouseId: main, deliveryItemId: delivery));
        await c.PostAsync<JsonElement>("/api/commerce/listings", new { itemId = plug, slug = "plug", isPublished = true });
        return new ReadyStore(t, slug, main, plug, box, delivery);
    }

    internal static async Task<(HttpClient Shopper, string Token)> CartAsync(ErpFixture fixture, string slug)
    {
        var shopper = fixture.Factory.CreateClient();
        var token = (await shopper.PostAsync<JsonElement>($"/api/store/{slug}/cart", new { })).GetProperty("token").GetString()!;
        shopper.DefaultRequestHeaders.Add("X-Cart-Token", token);
        return (shopper, token);
    }

    [Fact]
    public async Task A_cart_is_priced_like_the_till_and_knows_whether_the_store_can_supply_it()
    {
        var s = await ReadyStoreAsync(fixture);
        var (shopper, token) = await CartAsync(fixture, s.Slug);
        var url = $"/api/store/{s.Slug}/cart";
        (await shopper.GetAsync<JsonElement>(url)).GetProperty("lines").GetArrayLength().ShouldBe(0);

        await shopper.PutAsync<JsonElement>($"{url}/lines", new { productSlug = "plug", quantity = 2m });
        var cart = await shopper.PutAsync<JsonElement>($"{url}/lines", new { productSlug = "PLUG", uomCode = "box12", quantity = 1m });
        cart.GetProperty("subtotal").GetDecimal().ShouldBe(2 * 10.5m + 100m);
        cart.GetProperty("deliveryFee").GetDecimal().ShouldBe(15m);
        cart.GetProperty("total").GetDecimal().ShouldBe(136m);
        cart.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("uomCode").GetString(), l.GetProperty("amount").GetDecimal()))
            .ShouldBe([("PCS", 21m), ("BOX12", 100m)]);
        // 2 + 12 pieces wanted, 10 on hand: the store cannot supply it, without saying how many it has.
        cart.GetProperty("lines").EnumerateArray().ShouldAllBe(l => !l.GetProperty("enough").GetBoolean());
        cart.GetProperty("canCheckout").GetBoolean().ShouldBeFalse();
        (await shopper.GetStringAsync(url)).ShouldNotContain("onHand", Case.Insensitive);

        // Removing the box: enough stock, but under the 50 minimum order.
        cart = await shopper.PutAsync<JsonElement>($"{url}/lines", new { productSlug = "plug", uomCode = "BOX12", quantity = 0m });
        cart.GetProperty("lines").GetArrayLength().ShouldBe(1);
        cart.GetProperty("meetsMinimum").GetBoolean().ShouldBeFalse();
        cart = await shopper.PutAsync<JsonElement>($"{url}/lines", new { productSlug = "plug", quantity = 5m });
        cart.GetProperty("subtotal").GetDecimal().ShouldBe(52.5m);
        cart.GetProperty("canCheckout").GetBoolean().ShouldBeTrue();

        // Bad input.
        (await shopper.PutJsonAsync($"{url}/lines", new { productSlug = "nothing", quantity = 1m })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.PutJsonAsync($"{url}/lines", new { productSlug = "plug", uomCode = "CTN", quantity = 1m })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.PutJsonAsync($"{url}/lines", new { productSlug = "plug", quantity = -1m })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await shopper.PutJsonAsync($"{url}/lines", new { productSlug = "plug", quantity = 0.0001m })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // No key, a wrong key, or this store's key at another store: no cart.
        var anonymous = fixture.Factory.CreateClient();
        (await anonymous.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        anonymous.DefaultRequestHeaders.Add("X-Cart-Token", token + "x");
        (await anonymous.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var other = await ReadyStoreAsync(fixture);
        (await shopper.GetAsync($"/api/store/{other.Slug}/cart")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.PutJsonAsync($"/api/store/{other.Slug}/cart/lines", new { productSlug = "plug", quantity = 1m })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Stock promised to an open sales order is not available online: 10 on hand, 6 promised → the 5 in the cart are too many.
        var c = s.Tenant.Client;
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "B2B", nameEn = "Garage", nameAr = "كراج" })).GetProperty("id").GetGuid();
        var order = await c.PostAsync<JsonElement>("/api/sales/orders", new
        {
            customerId = customer, orderDate = FinanceTests.Today, warehouseId = s.Main, lines = new[] { new { itemId = s.Plug, quantity = 6m } }
        });
        await c.PostAsync<JsonElement>($"/api/sales/orders/{order.GetProperty("id").GetGuid()}/confirm", new { });
        cart = await shopper.GetAsync<JsonElement>(url);
        cart.GetProperty("lines")[0].GetProperty("enough").GetBoolean().ShouldBeFalse();
        cart.GetProperty("canCheckout").GetBoolean().ShouldBeFalse();
        (await shopper.GetAsync<JsonElement>($"/api/store/{s.Slug}/products/plug")).GetProperty("availability").GetString().ShouldBe("InStock");

        // A product taken off the store stays in the cart but no longer counts.
        var listing = (await c.GetAsync<List<JsonElement>>("/api/commerce/listings")).Single();
        await c.PutAsync<JsonElement>($"/api/commerce/listings/{listing.GetProperty("id").GetGuid()}", new { itemId = s.Plug, slug = "plug", isPublished = false });
        cart = await shopper.GetAsync<JsonElement>(url);
        cart.GetProperty("lines")[0].GetProperty("available").GetBoolean().ShouldBeFalse();
        cart.GetProperty("subtotal").GetDecimal().ShouldBe(0m);
        cart.GetProperty("deliveryFee").GetDecimal().ShouldBe(0m);
    }
}

using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>Cash-on-delivery checkout: the cart becomes a confirmed online sales order in one transaction, and stock is never oversold online.</summary>
[Collection(ErpCollection.Name)]
public sealed class CommerceCheckoutTests(ErpFixture fixture)
{
    internal static object Address(string name = "Sara Ahmed", string phone = "+974 5512 3456") => new
    {
        customerName = name, phone, zone = "25", street = "900", building = "12", unit = "3", notes = "Call on arrival", latitude = 25.2854m, longitude = 51.5310m
    };

    [Fact]
    public async Task A_cart_becomes_a_confirmed_online_sales_order_and_the_shopper_can_follow_it()
    {
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        var (shopper, _) = await CommerceCartTests.CartAsync(fixture, s.Slug);
        var store = $"/api/store/{s.Slug}";
        await shopper.PutAsync<JsonElement>($"{store}/cart/lines", new { productSlug = "plug", quantity = 2m });

        // Under the 50 minimum, and missing details, are refused before anything is saved.
        (await (await shopper.PostJsonAsync($"{store}/checkout", Address())).ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.below_minimum");
        await shopper.PutAsync<JsonElement>($"{store}/cart/lines", new { productSlug = "plug", quantity = 5m });
        (await (await shopper.PostJsonAsync($"{store}/checkout", Address(phone: "123"))).ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.phone_invalid");
        (await (await shopper.PostJsonAsync($"{store}/checkout", Address(name: " "))).ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.checkout_field");

        var placed = await shopper.PostAsync<JsonElement>($"{store}/checkout", Address());
        var orderNo = placed.GetProperty("orderNo").GetString()!;
        orderNo.ShouldStartWith("WEB");
        placed.GetProperty("total").GetDecimal().ShouldBe(5 * 10.5m + 15m);

        // In the ERP it is a confirmed sales order on channel online: the goods at the shop price and the delivery fee, for a
        // cash customer found by phone; the address is on the order.
        var c = s.Tenant.Client;
        var order = (await c.GetAsync<List<JsonElement>>("/api/sales/orders")).Single(o => o.GetProperty("externalRef").GetString() == orderNo);
        order.GetProperty("channel").GetString().ShouldBe("Online");
        order.GetProperty("status").GetString().ShouldBe("Confirmed");
        order.GetProperty("total").GetDecimal().ShouldBe(67.5m);
        order.GetProperty("customerCode").GetString().ShouldBe("WEB55123456");
        order.GetProperty("notes").GetString()!.ShouldContain("Zone 25, Street 900, Building 12, Unit 3");
        order.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("itemId").GetGuid(), l.GetProperty("amount").GetDecimal()))
            .ShouldBe([(s.Plug, 52.5m), (s.DeliveryItem, 15m)]);

        // The cart is used up; the 5 pieces are promised, so 5 are left to sell online.
        (await shopper.GetAsync($"{store}/cart")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.PostJsonAsync($"{store}/checkout", Address())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (next, _) = await CommerceCartTests.CartAsync(fixture, s.Slug);
        (await next.PutAsync<JsonElement>($"{store}/cart/lines", new { productSlug = "plug", quantity = 6m })).GetProperty("lines")[0]
            .GetProperty("enough").GetBoolean().ShouldBeFalse();

        // The shopper follows the order with the key from checkout only.
        var follow = fixture.Factory.CreateClient();
        (await follow.GetAsync($"{store}/orders/{orderNo}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        follow.DefaultRequestHeaders.Add("X-Order-Token", placed.GetProperty("statusToken").GetString());
        var status = await follow.GetAsync<JsonElement>($"{store}/orders/{orderNo.ToLowerInvariant()}");
        status.GetProperty("status").GetString().ShouldBe("Received");
        status.GetProperty("total").GetDecimal().ShouldBe(67.5m);
        status.GetProperty("lines")[0].GetProperty("titleEn").GetString().ShouldBe("Item PLUG");
        (await follow.GetAsync($"/api/store/{(await CommerceCartTests.ReadyStoreAsync(fixture)).Slug}/orders/{orderNo}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_store_without_a_dispatch_warehouse_takes_no_orders()
    {
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        await s.Tenant.Client.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(s.Slug, deliveryItemId: s.DeliveryItem));
        var (shopper, _) = await CommerceCartTests.CartAsync(fixture, s.Slug);
        var cart = await shopper.PutAsync<JsonElement>($"/api/store/{s.Slug}/cart/lines", new { productSlug = "plug", quantity = 5m });
        cart.GetProperty("canCheckout").GetBoolean().ShouldBeFalse();
        (await (await shopper.PostJsonAsync($"/api/store/{s.Slug}/checkout", Address())).ProblemAsync()).GetProperty("code").GetString()
            .ShouldBe("commerce.checkout_closed");
    }

    [Fact]
    public async Task Shoppers_racing_for_the_last_box_never_oversell()
    {
        // 12 pieces: exactly one box. Six shoppers try to buy it at the same moment.
        var s = await CommerceCartTests.ReadyStoreAsync(fixture, stock: 12m);
        var carts = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
        {
            var (shopper, _) = await CommerceCartTests.CartAsync(fixture, s.Slug);
            await shopper.PutAsync<JsonElement>($"/api/store/{s.Slug}/cart/lines", new { productSlug = "plug", uomCode = "BOX12", quantity = 1m });
            carts.Add(shopper);
        }
        var results = await Task.WhenAll(carts.Select((shopper, i) =>
            shopper.PostJsonAsync($"/api/store/{s.Slug}/checkout", Address($"Shopper {i}", $"5560000{i}"))));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var refused in results.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.out_of_stock");
        }
        (await s.Tenant.Client.GetAsync<List<JsonElement>>("/api/sales/orders")).Count(o => o.GetProperty("channel").GetString() == "Online").ShouldBe(1);
        (await fixture.Factory.CreateClient().GetAsync<JsonElement>($"/api/store/{s.Slug}/products/plug")).GetProperty("availability").GetString()
            .ShouldBe("OutOfStock");
    }
}

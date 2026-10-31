using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;
using Npgsql;

namespace Erp.IntegrationTests;

/// <summary>
/// Phase 2's "done when" (design §12): a shop takes a cash-on-delivery order online and delivers it, and stock and the ledger match.
/// Also the release gate for carts and orders: another store's shoppers and staff never reach them.
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class CommerceCodFlowTests(ErpFixture fixture)
{
    private static async Task<FinanceTests.TbRow> RowAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={FinanceTests.Today:yyyy-MM-dd}&to={FinanceTests.Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code) ?? new FinanceTests.TbRow(code, 0, 0, 0, 0);

    private static async Task<(string OrderNo, string Token)> OrderAsync(ErpFixture fixture, string slug, decimal quantity, string? uom = null)
    {
        var (shopper, _) = await CommerceCartTests.CartAsync(fixture, slug);
        await shopper.PutAsync<JsonElement>($"/api/store/{slug}/cart/lines", new { productSlug = "plug", uomCode = uom, quantity });
        var placed = await shopper.PostAsync<JsonElement>($"/api/store/{slug}/checkout", CommerceCheckoutTests.Address());
        return (placed.GetProperty("orderNo").GetString()!, placed.GetProperty("statusToken").GetString()!);
    }

    [Fact]
    public async Task A_cash_on_delivery_order_is_delivered_invoiced_and_paid_with_stock_and_ledger_matching()
    {
        // 10 plugs at a cost of 6; the shopper buys 5 at 10.5 plus the 15 delivery fee.
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        var c = s.Tenant.Client;
        var (orderNo, token) = await OrderAsync(fixture, s.Slug, 5m);
        var queued = (await c.GetAsync<List<JsonElement>>("/api/commerce/orders")).Single();
        queued.GetProperty("orderNo").GetString().ShouldBe(orderNo);
        queued.GetProperty("stage").GetString().ShouldBe("New");
        queued.GetProperty("address").GetString().ShouldBe("Zone 25, Street 900, Building 12, Unit 3");
        queued.GetProperty("total").GetDecimal().ShouldBe(67.5m);
        // Nothing has left the shop and nothing is in the books yet: an order is a promise.
        (await RowAsync(c, "4110")).Credit.ShouldBe(0m);

        // Deliver (the screen's steps): a delivery note for the goods, one invoice for goods and delivery fee.
        var orderId = queued.GetProperty("salesOrderId").GetGuid();
        var order = await c.GetAsync<JsonElement>($"/api/sales/orders/{orderId}");
        var lines = order.GetProperty("lines").EnumerateArray().ToList();
        var goods = lines.Single(l => l.GetProperty("itemType").GetString() == "Stock");
        var fee = lines.Single(l => l.GetProperty("itemType").GetString() == "Service");
        var note = await c.PostAsync<JsonElement>("/api/sales/deliveries", new
        {
            salesOrderId = orderId, deliveryDate = FinanceTests.Today, deliveryAddress = queued.GetProperty("address").GetString(),
            lines = new[] { new { salesOrderLineId = goods.GetProperty("id").GetGuid(), quantity = 5m } }
        });
        var invoice = await c.PostAsync<JsonElement>("/api/sales/invoices", new
        {
            customerId = order.GetProperty("customerId").GetGuid(), invoiceDate = FinanceTests.Today, customerRef = orderNo,
            lines = new object[]
            {
                new { deliveryNoteLineId = note.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 5m },
                new { salesOrderLineId = fee.GetProperty("id").GetGuid(), quantity = 1m }
            }
        });
        invoice = await c.PostAsync<JsonElement>($"/api/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
        invoice.GetProperty("total").GetDecimal().ShouldBe(67.5m);
        var toCollect = (await c.GetAsync<List<JsonElement>>("/api/commerce/orders")).Single();
        toCollect.GetProperty("stage").GetString().ShouldBe("ToCollect");
        toCollect.GetProperty("outstanding").GetDecimal().ShouldBe(67.5m);

        // The shopper sees it delivered.
        var follow = fixture.Factory.CreateClient();
        follow.DefaultRequestHeaders.Add("X-Order-Token", token);
        (await follow.GetAsync<JsonElement>($"/api/store/{s.Slug}/orders/{orderNo}")).GetProperty("status").GetString().ShouldBe("Delivered");

        // The driver hands over the cash.
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        await c.PostAsync<JsonElement>("/api/sales/receipts", new
        {
            customerId = order.GetProperty("customerId").GetGuid(), date = FinanceTests.Today, method = "Cash", depositAccountId = accounts.Single(a => a.Code == "1110").Id,
            reference = orderNo, allocations = new[] { new { salesInvoiceId = invoice.GetProperty("id").GetGuid(), amount = 67.5m } }
        });
        (await c.GetAsync<List<JsonElement>>("/api/commerce/orders")).ShouldBeEmpty();
        (await c.GetAsync<List<JsonElement>>("/api/commerce/orders?stage=done")).Single().GetProperty("stage").GetString().ShouldBe("Done");

        // Stock: 5 left, valued at cost; the books: revenue for goods and delivery, cost of the goods, cash in, nothing owed.
        var stock = (await c.GetAsync<List<JsonElement>>("/api/inventory/stock")).Single(r => r.GetProperty("itemId").GetGuid() == s.Plug);
        stock.GetProperty("quantity").GetDecimal().ShouldBe(5m);
        (await RowAsync(c, "4110")).Credit.ShouldBe(52.5m);
        (await RowAsync(c, "4120")).Credit.ShouldBe(15m);
        (await RowAsync(c, "5110")).Debit.ShouldBe(30m);
        var inventory = await RowAsync(c, "1150");
        (inventory.Debit - inventory.Credit).ShouldBe(30m);
        (await RowAsync(c, "1110")).Debit.ShouldBe(67.5m);
        var receivable = await RowAsync(c, "1130");
        (receivable.Debit - receivable.Credit).ShouldBe(0m);
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={FinanceTests.Today:yyyy-MM-dd}&to={FinanceTests.Today:yyyy-MM-dd}"))
            .IsBalanced.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_stores_carts_and_orders_are_out_of_reach()
    {
        var a = await CommerceCartTests.ReadyStoreAsync(fixture);
        var b = await CommerceCartTests.ReadyStoreAsync(fixture);
        var (orderB, tokenB) = await OrderAsync(fixture, b.Slug, 5m);
        var (shopperB, cartB) = await CommerceCartTests.CartAsync(fixture, b.Slug);
        await shopperB.PutAsync<JsonElement>($"/api/store/{b.Slug}/cart/lines", new { productSlug = "plug", quantity = 1m });

        // B's order number and status key, and B's cart key, mean nothing at store A.
        var follow = fixture.Factory.CreateClient();
        follow.DefaultRequestHeaders.Add("X-Order-Token", tokenB);
        follow.DefaultRequestHeaders.Add("X-Cart-Token", cartB);
        (await follow.GetAsync($"/api/store/{a.Slug}/orders/{orderB}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await follow.GetAsync($"/api/store/{a.Slug}/cart")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await follow.PostJsonAsync($"/api/store/{a.Slug}/checkout", CommerceCheckoutTests.Address())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await follow.GetAsync($"/api/store/{b.Slug}/orders/{orderB}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A's staff see none of B's orders; B's sales order id is unknown to A's Sales.
        (await a.Tenant.Client.GetAsync<List<JsonElement>>("/api/commerce/orders?stage=all")).ShouldBeEmpty();
        var bOrder = (await b.Tenant.Client.GetAsync<List<JsonElement>>("/api/commerce/orders")).Single().GetProperty("salesOrderId").GetGuid();
        (await a.Tenant.Client.GetAsync($"/api/sales/orders/{bOrder}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And the database itself keeps carts, checkouts and holds per tenant.
        await using var asA = await fixture.OpenAppConnectionAsync(a.Tenant.TenantId);
        foreach (var table in new[] { "cart", "cart_line", "checkout", "reservation" })
            ((long)(await new NpgsqlCommand($"SELECT count(*) FROM commerce.{table} WHERE tenant_id = '{b.Tenant.TenantId}'", asA).ExecuteScalarAsync())!).ShouldBe(0);
    }
}

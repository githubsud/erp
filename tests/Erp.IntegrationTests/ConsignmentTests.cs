using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>
/// Goods on trust: a partner shop's perfumes are received at no cost, sold as its agent (only the commission is income, the rest is owed to the
/// shop), returned, settled into one supplier invoice and paid. Stock value never includes the shop's goods.
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class ConsignmentTests(ErpFixture fixture)
{
    private static DateOnly Today => FinanceTests.Today;

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    private static async Task<decimal> OnHandAsync(HttpClient c, Guid shop, Guid item) =>
        (await c.GetAsync<List<JsonElement>>($"/api/purchasing/consignment/shops/{shop}/items"))
        .Single(i => i.GetProperty("itemId").GetGuid() == item).GetProperty("onHand").GetDecimal();

    [Fact]
    public async Task A_partner_shops_perfumes_are_sold_for_a_commission_and_settled_monthly()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var oud = await CommerceAdminTests.ItemAsync(c, "OUD-12", 250m);
        var musk = await CommerceAdminTests.ItemAsync(c, "MUSK-6", 80m);
        var shop = await PurchasingTests.SupplierAsync(c, "SHOP1");

        // A supplier is not a partner shop until its agreement is saved.
        (await (await c.PostJsonAsync("/api/purchasing/consignment/receipts", new
        {
            supplierId = shop, warehouseId = main, date = Today, lines = new[] { new { itemId = oud, quantity = 1m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.not_partner_shop");

        // 10% of the sale; musk at a fixed QAR 15 a bottle.
        await c.PutAsync<JsonElement>("/api/purchasing/consignment/shops", new
        {
            supplierId = shop, commissionType = "Percent", commission = 10m, startsOn = Today.AddDays(-5), payoutDay = 5
        });
        await c.PostAsync<JsonElement>($"/api/purchasing/consignment/shops/{shop}/items", new { itemIds = new[] { oud } });
        var items = await c.PostAsync<List<JsonElement>>($"/api/purchasing/consignment/shops/{shop}/items",
            new { itemIds = new[] { musk }, commissionType = "Fixed", commission = 15m });
        items.Count.ShouldBe(2);

        // Bottles on trust: in stock, but worth nothing in our books.
        var receipt = await c.PostAsync<JsonElement>("/api/purchasing/consignment/receipts", new
        {
            supplierId = shop, warehouseId = main, date = Today, notes = "Signed by the shop",
            lines = new[] { new { itemId = oud, quantity = 6m }, new { itemId = musk, quantity = 10m } }
        });
        receipt.GetProperty("movementNo").GetString()!.ShouldStartWith("GTR");
        (await OnHandAsync(c, shop, oud)).ShouldBe(6m);
        (await ClosingAsync(c, "1150")).ShouldBe(0m);

        // The shop's goods cannot be bought, received at a cost, or change owner while in stock.
        (await (await c.PostJsonAsync("/api/purchasing/orders", new
        {
            supplierId = shop, orderDate = Today, warehouseId = main, lines = new[] { new { itemId = oud, quantity = 1m, unitPrice = 100m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.item_on_trust");
        (await (await c.PostJsonAsync($"/api/purchasing/consignment/shops/{shop}/items/release", new { itemIds = new[] { oud } })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("inv.consignment_in_stock");

        // A sale: 2 oud (QAR 500, 10% = 50) and 3 musk (QAR 240, 3 × 15 = 45). Only the commission is income; the rest is owed to the shop.
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C1", nameEn = "Shopper", nameAr = "متسوق", paymentTermsDays = 0 }))
            .GetProperty("id").GetGuid();
        var invoice = await c.PostAsync<SalesTests.Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, warehouseId = main,
            lines = new[] { new { itemId = oud, quantity = 2m }, new { itemId = musk, quantity = 3m } }
        });
        invoice = await c.PostAsync<SalesTests.Invoice>($"/api/sales/invoices/{invoice.Id}/post", new { });
        invoice.Total.ShouldBe(740m);
        (await ClosingAsync(c, "4140")).ShouldBe(-95m);
        (await ClosingAsync(c, "2185")).ShouldBe(-645m);
        (await ClosingAsync(c, "4110")).ShouldBe(0m);
        (await ClosingAsync(c, "5110")).ShouldBe(0m);
        (await ClosingAsync(c, "1150")).ShouldBe(0m);

        // One oud comes back: the shop gives back its 225 and we give back half our commission on that line (25).
        await c.PostAsync<SalesTests.CreditNote>("/api/sales/credit-notes", new
        {
            salesInvoiceId = invoice.Id, date = Today, reason = "Changed mind",
            lines = new[] { new { lineType = "Return", salesInvoiceLineId = invoice.Lines.Single(l => l.Amount == 500m).Id, quantity = 1m } }
        });
        (await ClosingAsync(c, "4140")).ShouldBe(-70m);
        (await ClosingAsync(c, "2185")).ShouldBe(-420m);
        (await OnHandAsync(c, shop, oud)).ShouldBe(5m);

        // The statement up to today: what was sold, our commission and the shop's share.
        var preview = await c.GetAsync<JsonElement>($"/api/purchasing/consignment/statement?supplierId={shop}&periodEnd={Today:yyyy-MM-dd}");
        var lines = preview.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("sku").GetString()!);
        lines["OUD-12"].GetProperty("sold").GetDecimal().ShouldBe(2m);
        lines["OUD-12"].GetProperty("returned").GetDecimal().ShouldBe(1m);
        lines["OUD-12"].GetProperty("due").GetDecimal().ShouldBe(225m);
        lines["MUSK-6"].GetProperty("commission").GetDecimal().ShouldBe(45m);
        preview.GetProperty("sales").GetDecimal().ShouldBe(490m);
        preview.GetProperty("commission").GetDecimal().ShouldBe(70m);
        preview.GetProperty("adjustments").GetDecimal().ShouldBe(0m);
        preview.GetProperty("amount").GetDecimal().ShouldBe(420m);

        // Settling turns it into a supplier invoice; the same period cannot be settled twice.
        var settled = await c.PostAsync<JsonElement>("/api/purchasing/consignment/settlements", new { supplierId = shop, periodEnd = Today });
        settled.GetProperty("settlementNo").GetString()!.ShouldStartWith("CST");
        settled.GetProperty("amount").GetDecimal().ShouldBe(420m);
        var supplierInvoiceId = settled.GetProperty("supplierInvoiceId").GetGuid();
        (await ClosingAsync(c, "2185")).ShouldBe(0m);
        (await ClosingAsync(c, "2110")).ShouldBe(-420m);
        (await (await c.PostJsonAsync("/api/purchasing/consignment/settlements", new { supplierId = shop, periodEnd = Today })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("pur.settlement_period");
        var shopRow = (await c.GetAsync<List<JsonElement>>("/api/purchasing/consignment/shops")).Single();
        shopRow.GetProperty("owed").GetDecimal().ShouldBe(0m);
        shopRow.GetProperty("onHand").GetDecimal().ShouldBe(12m);

        // Paid like any supplier invoice.
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;
        await c.PostAsync<PurchasingTests.Payment>("/api/purchasing/payments", new
        {
            supplierId = shop, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId, amount = 420m } }
        });
        (await c.GetAsync<PurchasingTests.Invoice>($"/api/purchasing/invoices/{supplierInvoiceId}")).Status.ShouldBe("Paid");
        (await c.GetAsync<List<JsonElement>>($"/api/purchasing/consignment/settlements?supplierId={shop}")).Single()
            .GetProperty("invoiceStatus").GetString().ShouldBe("Paid");

        // The unsold oud goes back to the shop; then oud can become ours again.
        await c.PostAsync<JsonElement>("/api/purchasing/consignment/returns", new
        {
            supplierId = shop, warehouseId = main, date = Today, lines = new[] { new { itemId = oud, quantity = 5m } }
        });
        (await OnHandAsync(c, shop, oud)).ShouldBe(0m);
        (await c.PostAsync<List<JsonElement>>($"/api/purchasing/consignment/shops/{shop}/items/release", new { itemIds = new[] { oud } })).Count.ShouldBe(1);

        var month = (await c.GetAsync<List<JsonElement>>(
            $"/api/purchasing/consignment/commissions?periodStart={Today.AddDays(-31):yyyy-MM-dd}&periodEnd={Today:yyyy-MM-dd}")).Single();
        month.GetProperty("commission").GetDecimal().ShouldBe(70m);
        month.GetProperty("due").GetDecimal().ShouldBe(420m);

        // Another company sees nothing of this shop.
        var other = await fixture.CreateTenantAsync();
        (await other.Client.GetAsync($"/api/purchasing/consignment/shops/{shop}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Client.GetAsync<List<JsonElement>>("/api/purchasing/consignment/shops")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Goods_on_trust_are_received_at_no_cost_and_a_bad_movement_is_reversed()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var oud = await CommerceAdminTests.ItemAsync(c, "OUD-3", 90m);
        var shop = await PurchasingTests.SupplierAsync(c, "SHOP2");
        await c.PutAsync<JsonElement>("/api/purchasing/consignment/shops", new { supplierId = shop, commissionType = "Percent", commission = 150m, startsOn = Today })
            .ShouldThrowAsync<Exception>();
        await c.PutAsync<JsonElement>("/api/purchasing/consignment/shops", new { supplierId = shop, commissionType = "Percent", commission = 12.5m, startsOn = Today });
        await c.PostAsync<JsonElement>($"/api/purchasing/consignment/shops/{shop}/items", new { itemIds = new[] { oud } });

        // A stock adjustment with a cost on a shop's item is refused: its bottles are never ours.
        (await (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Count", lines = new[] { new { itemId = oud, quantity = 2m, unitCost = 40m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.consignment_cost");

        var receipt = await c.PostAsync<JsonElement>("/api/purchasing/consignment/receipts", new
        {
            supplierId = shop, warehouseId = main, date = Today, lines = new[] { new { itemId = oud, quantity = 4m } }
        });
        // Returning more than we hold is refused; reversing the receipt takes the bottles out again.
        (await c.PostJsonAsync("/api/purchasing/consignment/returns", new
        {
            supplierId = shop, warehouseId = main, date = Today, lines = new[] { new { itemId = oud, quantity = 5m } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var reversed = await c.PostAsync<JsonElement>($"/api/purchasing/consignment/movements/{receipt.GetProperty("id").GetGuid()}/reverse",
            new { date = Today, reason = "Wrong shop" });
        reversed.GetProperty("status").GetString().ShouldBe("Reversed");
        (await OnHandAsync(c, shop, oud)).ShouldBe(0m);

        // Nothing sold: nothing to settle.
        (await (await c.PostJsonAsync("/api/purchasing/consignment/settlements", new { supplierId = shop, periodEnd = Today })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("pur.settlement_nothing");
    }
}

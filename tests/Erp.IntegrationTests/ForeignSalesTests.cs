using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class ForeignSalesTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Invoice(Guid Id, string InvoiceNo, string Status, string Currency, decimal ExchangeRate, decimal Total, decimal TotalBase,
        decimal OpenAmount, decimal OpenBase);
    public sealed record Aging(decimal Total, decimal LedgerBalance);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Euro_customer_is_quoted_invoiced_and_paid_with_exchange_gains_and_losses_in_qar()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var design = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "DESIGN", nameEn = "Interior design", nameAr = "تصميم داخلي", itemType = "Service", itemGroupId = group, uomId = pcs, salesPrice = 400m
        })).Id;
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new
        {
            code = "C-DE", nameEn = "Berlin GmbH", nameAr = "شركة برلين", paymentTermsDays = 30, currency = "EUR"
        })).GetProperty("id").GetGuid();
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;

        // The order is in the customer's currency; the QAR list price of 400 becomes EUR 100 at 4.00.
        (await (await c.PostJsonAsync("/api/sales/orders", new
        {
            customerId = customer, orderDate = Today, warehouseId = main, lines = new[] { new { itemId = design, quantity = 10m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.exchange_rate");
        var order = await c.PostAsync<JsonElement>("/api/sales/orders", new
        {
            customerId = customer, orderDate = Today, warehouseId = main, exchangeRate = 4.00m, lines = new[] { new { itemId = design, quantity = 10m } }
        });
        order.GetProperty("currency").GetString().ShouldBe("EUR");
        order.GetProperty("total").GetDecimal().ShouldBe(1_000m);
        var orderId = order.GetProperty("id").GetGuid();
        await c.PostAsync<JsonElement>($"/api/sales/orders/{orderId}/confirm", new { });
        var lineId = (await c.GetAsync<JsonElement>($"/api/sales/orders/{orderId}")).GetProperty("lines")[0].GetProperty("id").GetGuid();

        // Invoiced at 4.05: EUR 1,000 = QAR 4,050 of revenue and receivable.
        var invoice = await c.PostAsync<Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, exchangeRate = 4.05m, lines = new[] { new { salesOrderLineId = lineId, quantity = 10m } }
        });
        invoice.Currency.ShouldBe("EUR");
        invoice.TotalBase.ShouldBe(4_050m);
        invoice = await c.PostAsync<Invoice>($"/api/sales/invoices/{invoice.Id}/post", new { });
        (await ClosingAsync(c, "1130")).ShouldBe(4_050m);
        (await ClosingAsync(c, "4120")).ShouldBe(-4_050m);

        // EUR 600 received at 4.10 (QAR 30 gain), the remaining EUR 400 at 3.95 (QAR 40 loss).
        var first = await c.PostAsync<JsonElement>("/api/sales/receipts", new
        {
            customerId = customer, date = Today, method = "BankTransfer", depositAccountId = bank, exchangeRate = 4.10m,
            allocations = new[] { new { salesInvoiceId = invoice.Id, amount = 600m } }
        });
        first.GetProperty("fxDifference").GetDecimal().ShouldBe(30m);
        first.GetProperty("amountBase").GetDecimal().ShouldBe(2_460m);
        (await (await c.PostJsonAsync("/api/sales/credit-notes", new
        {
            salesInvoiceId = invoice.Id, date = Today, reason = "Too much",
            lines = new[] { new { lineType = "Allowance", description = "x", quantity = 1m, unitPrice = 500m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.credit_exceeds_open");
        var second = await c.PostAsync<JsonElement>("/api/sales/receipts", new
        {
            customerId = customer, date = Today, method = "BankTransfer", depositAccountId = bank, exchangeRate = 3.95m,
            allocations = new[] { new { salesInvoiceId = invoice.Id, amount = 400m } }
        });
        second.GetProperty("fxDifference").GetDecimal().ShouldBe(-40m);
        var paid = await c.GetAsync<Invoice>($"/api/sales/invoices/{invoice.Id}");
        paid.Status.ShouldBe("Paid");
        paid.OpenBase.ShouldBe(0m);
        (await ClosingAsync(c, "1130")).ShouldBe(0m);
        (await ClosingAsync(c, "1120")).ShouldBe(4_040m);
        (await ClosingAsync(c, "4220")).ShouldBe(10m);

        // Reversing the second receipt reopens EUR 400 / QAR 1,620 and the aging shows it in QAR.
        await c.PostAsync<JsonElement>($"/api/sales/receipts/{second.GetProperty("id").GetGuid()}/reverse", new { date = Today, reason = "Recalled" });
        var reopened = await c.GetAsync<Invoice>($"/api/sales/invoices/{invoice.Id}");
        reopened.OpenAmount.ShouldBe(400m);
        reopened.OpenBase.ShouldBe(1_620m);
        var aging = await c.GetAsync<Aging>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.Total.ShouldBe(1_620m);
        aging.Total.ShouldBe(aging.LedgerBalance);
    }
}

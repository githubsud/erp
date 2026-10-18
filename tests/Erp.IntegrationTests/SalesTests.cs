using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SalesTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Line(Guid Id, Guid ItemId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal Amount, decimal QuantityDelivered,
        decimal QuantityInvoiced);
    public sealed record Quote(Guid Id, string QuoteNo, string Status, decimal Total, Guid? SalesOrderId, IReadOnlyList<Line> Lines);
    public sealed record Order(Guid Id, string SoNo, string Status, decimal Total, string? CreditWarning, IReadOnlyList<Line> Lines);
    public sealed record DeliveryLine(Guid Id, Guid ItemId, decimal Quantity, decimal UnitCost, decimal Cost);
    public sealed record Delivery(Guid Id, string DnNo, string Status, decimal TotalCost, IReadOnlyList<DeliveryLine> Lines);
    public sealed record InvoiceLine(Guid Id, string LineType, decimal Quantity, decimal Amount, decimal Cost);
    public sealed record Invoice(Guid Id, string InvoiceNo, string Status, decimal Total, decimal DiscountTotal, decimal OpenAmount, Guid? EInvoiceUuid,
        IReadOnlyList<InvoiceLine> Lines);
    public sealed record CreditNote(Guid Id, string CreditNoteNo, decimal Total, decimal CostReturned, string Status);
    public sealed record Receipt(Guid Id, string ReceiptNo, string Status, decimal Amount);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    private static async Task<Guid> CustomerAsync(HttpClient c, string code, decimal? creditLimit) =>
        (await c.PostAsync<JsonElement>("/api/sales/customers", new
        {
            code, nameEn = $"Customer {code}", nameAr = "عميل", paymentTermsDays = 30, creditLimit
        })).GetProperty("id").GetGuid();

    private static async Task<Guid> PricedItemAsync(HttpClient c, string sku, string type, decimal salesPrice)
    {
        var (_, group, pcs) = await InventoryTests.DefaultsAsync(c);
        return (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku, nameEn = $"Item {sku}", nameAr = "صنف", itemType = type, itemGroupId = group, uomId = pcs, salesPrice
        })).Id;
    }

    private static Task<HttpResponseMessage> DeliverAsync(HttpClient c, Order order, int line, decimal quantity) =>
        c.PostJsonAsync("/api/sales/deliveries", new
        {
            salesOrderId = order.Id, deliveryDate = Today, lines = new[] { new { salesOrderLineId = order.Lines[line].Id, quantity } }
        });

    [Fact]
    public async Task Order_to_cash_with_credit_control_returns_and_a_bounced_cheque_reconciles_to_the_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var pipe = await PricedItemAsync(c, "PIPE", "Stock", 80m);
        var install = await PricedItemAsync(c, "INSTALL", "Service", 200m);
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;
        await (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new[] { new { itemId = pipe, quantity = 20m, unitCost = 50m } }
        })).EnsureSuccessAsync();
        var customer = await CustomerAsync(c, "C-ALPHA", 2_000m);

        // Quotation at the item's sales price with a 10% discount on the pipes, converted into a draft order.
        var quote = await c.PostAsync<Quote>("/api/sales/quotations", new
        {
            customerId = customer, quoteDate = Today,
            lines = new object[] { new { itemId = pipe, quantity = 10m, discountPercent = 10m }, new { itemId = install, quantity = 1m } }
        });
        quote.Total.ShouldBe(920m);                       // 10 × 80 × 90% + 200
        quote.Lines[0].UnitPrice.ShouldBe(80m);
        await c.PostAsync<Quote>($"/api/sales/quotations/{quote.Id}/send", new { });
        var order = await c.PostAsync<Order>($"/api/sales/quotations/{quote.Id}/convert", new { warehouseId = main, orderDate = Today });
        order.Status.ShouldBe("Draft");
        (await c.GetAsync<Quote>($"/api/sales/quotations/{quote.Id}")).Status.ShouldBe("Accepted");

        // Within the 2,000 limit the order is confirmed; a second order would exceed it and is held for a manager.
        order = await c.PostAsync<Order>($"/api/sales/orders/{order.Id}/confirm", new { });
        order.Status.ShouldBe("Confirmed");
        var big = await c.PostAsync<Order>("/api/sales/orders", new
        {
            customerId = customer, orderDate = Today, warehouseId = main, lines = new[] { new { itemId = pipe, quantity = 20m } }
        });
        big = await c.PostAsync<Order>($"/api/sales/orders/{big.Id}/confirm", new { });
        big.Status.ShouldBe("CreditHold");
        (await DeliverAsync(c, big, 0, 1m)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.PostAsync<Order>($"/api/sales/orders/{big.Id}/approve-credit", new { })).Status.ShouldBe("Confirmed");
        (await c.PostAsync<Order>($"/api/sales/orders/{big.Id}/cancel", new { })).Status.ShouldBe("Cancelled");

        // Deliver 6 of 10 pipes at average cost 50: Dr Cost of Sales 300 / Cr Inventory 300. Over-delivery is refused.
        var dn = await (await DeliverAsync(c, order, 0, 6m)).ReadAsync<Delivery>();
        dn.TotalCost.ShouldBe(300m);
        (await DeliverAsync(c, order, 0, 5m)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DeliverAsync(c, order, 1, 1m)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // services are not delivered
        (await c.GetAsync<Order>($"/api/sales/orders/{order.Id}")).Status.ShouldBe("PartiallyDelivered");
        (await ClosingAsync(c, "5110")).ShouldBe(300m);

        // Invoice the delivered pipes (order price and discount) and the installation service from the order.
        var invoice = await c.PostAsync<Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today,
            lines = new object[]
            {
                new { deliveryNoteLineId = dn.Lines[0].Id, quantity = 6m },
                new { salesOrderLineId = order.Lines[1].Id, quantity = 1m }
            }
        });
        invoice.Total.ShouldBe(632m);                     // 6 × 72 + 200
        invoice.DiscountTotal.ShouldBe(48m);
        invoice.Status.ShouldBe("Draft");
        invoice = await c.PostAsync<Invoice>($"/api/sales/invoices/{invoice.Id}/post", new { });
        invoice.Status.ShouldBe("Posted");
        invoice.EInvoiceUuid.ShouldNotBeNull();
        (await ClosingAsync(c, "1130")).ShouldBe(632m);
        (await ClosingAsync(c, "4110")).ShouldBe(-432m);
        (await ClosingAsync(c, "4120")).ShouldBe(-200m);
        (await c.PostJsonAsync($"/api/sales/deliveries/{dn.Id}/reverse", new { date = Today, reason = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // already invoiced

        // Two pipes come back: Dr Sales Returns 144 / Cr Receivable; stock back at the cost it left with (Dr Inventory 100 / Cr COGS 100).
        var credit = await c.PostAsync<CreditNote>("/api/sales/credit-notes", new
        {
            salesInvoiceId = invoice.Id, date = Today, reason = "Damaged in transit",
            lines = new[] { new { lineType = "Return", salesInvoiceLineId = invoice.Lines[0].Id, quantity = 2m } }
        });
        credit.Total.ShouldBe(144m);
        credit.CostReturned.ShouldBe(100m);
        (await ClosingAsync(c, "4130")).ShouldBe(144m);
        (await ClosingAsync(c, "5110")).ShouldBe(200m);
        (await ClosingAsync(c, "1150")).ShouldBe(800m);
        (await c.GetAsync<Invoice>($"/api/sales/invoices/{invoice.Id}")).OpenAmount.ShouldBe(488m);

        // A post-dated cheque: held in PDC receivable, cannot be deposited early, then bounces after depositing.
        var receiptDate = Today.Day > 1 ? Today.AddDays(-1) : Today;
        var pdc = await c.PostAsync<Receipt>("/api/sales/receipts", new
        {
            customerId = customer, date = receiptDate, method = "PostDatedCheque", chequeNo = "000451", chequeDate = receiptDate.AddDays(1),
            chequeBank = "QNB", allocations = new[] { new { salesInvoiceId = invoice.Id, amount = 300m } }
        });
        pdc.Status.ShouldBe("PdcHeld");
        (await ClosingAsync(c, "1140")).ShouldBe(300m);
        var exposure = await c.GetAsync<JsonElement>($"/api/sales/customers/{customer}/exposure");
        exposure.GetProperty("chequesInHand").GetDecimal().ShouldBe(300m);
        exposure.GetProperty("receivable").GetDecimal().ShouldBe(188m);
        (await c.PostJsonAsync($"/api/sales/receipts/{pdc.Id}/deposit", new { date = receiptDate, bankAccountId = bank }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.PostAsync<Receipt>($"/api/sales/receipts/{pdc.Id}/deposit", new { date = receiptDate.AddDays(1), bankAccountId = bank }))
            .Status.ShouldBe("PdcCleared");
        (await ClosingAsync(c, "1140")).ShouldBe(0m);
        (await c.PostAsync<Receipt>($"/api/sales/receipts/{pdc.Id}/bounce", new { date = receiptDate.AddDays(1), reason = "Insufficient funds" }))
            .Status.ShouldBe("Bounced");
        (await c.GetAsync<Invoice>($"/api/sales/invoices/{invoice.Id}")).OpenAmount.ShouldBe(488m);
        (await ClosingAsync(c, "1120")).ShouldBe(0m);

        // Paid by bank transfer; the invoice can no longer be reversed.
        await c.PostAsync<Receipt>("/api/sales/receipts", new
        {
            customerId = customer, date = Today, method = "BankTransfer", depositAccountId = bank, reference = "TT-1",
            allocations = new[] { new { salesInvoiceId = invoice.Id, amount = 488m } }
        });
        (await c.GetAsync<Invoice>($"/api/sales/invoices/{invoice.Id}")).Status.ShouldBe("Paid");
        (await c.PostJsonAsync($"/api/sales/invoices/{invoice.Id}/reverse", new { date = Today, reason = "x" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // A counter sale issues stock from the invoice itself.
        var counter = await c.PostAsync<Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, warehouseId = main, lines = new[] { new { itemId = pipe, quantity = 3m } }
        });
        counter.Lines[0].LineType.ShouldBe("Direct");
        counter = await c.PostAsync<Invoice>($"/api/sales/invoices/{counter.Id}/post", new { });
        counter.Lines[0].Cost.ShouldBe(150m);
        (await ClosingAsync(c, "1150")).ShouldBe(650m);

        // Reports reconcile: aging = receivable account, statement closing = receivable, stock = inventory account.
        var aging = await c.GetAsync<JsonElement>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(240m);
        aging.GetProperty("ledgerBalance").GetDecimal().ShouldBe(240m);
        var statement = await c.GetAsync<JsonElement>($"/api/sales/customers/{customer}/statement?from={Today.AddDays(-5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        statement.GetProperty("closingBalance").GetDecimal().ShouldBe(240m);
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.Difference.ShouldBe(0m);
        var analysis = await c.GetAsync<JsonElement>($"/api/sales/reports/analysis?from={Today.AddDays(-5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        analysis.GetProperty("netRevenue").GetDecimal().ShouldBe(632m - 144m + 240m);
        analysis.GetProperty("cost").GetDecimal().ShouldBe(300m - 100m + 150m);
        var uninvoiced = await c.GetAsync<JsonElement>("/api/sales/reports/uninvoiced");
        uninvoiced.GetArrayLength().ShouldBe(0);
        (await c.GetAsync($"/api/sales/invoices/{invoice.Id}/pdf")).Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today.AddDays(-5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
    }

    [Fact]
    public async Task Cash_only_customer_cannot_be_invoiced_on_credit_without_a_manager()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var service = await PricedItemAsync(c, "SVC", "Service", 100m);
        var customer = await CustomerAsync(c, "C-CASH", 0m);
        var invoice = await c.PostAsync<Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, lines = new[] { new { itemId = service, quantity = 1m } }
        });

        // A salesperson (no credit release permission) is refused; the owner may post it.
        await (await c.PostJsonAsync("/api/core/users", new
        {
            email = "sales@test.qa", displayNameEn = "Sales", displayNameAr = "مبيعات", password = "Sales-Password-123", preferredLanguage = "en",
            roles = new[] { "salesperson" }
        })).EnsureSuccessAsync();
        var sales = await fixture.LoginAsync(t.Code, "sales@test.qa", "Sales-Password-123");
        var refused = await sales.PostJsonAsync($"/api/sales/invoices/{invoice.Id}/post", new { });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.credit_limit_exceeded");
        (await c.PostAsync<Invoice>($"/api/sales/invoices/{invoice.Id}/post", new { })).Status.ShouldBe("Posted");

        // Under the "warn" policy the order is confirmed with a warning instead of being held.
        await c.PutAsync<JsonElement>("/api/sales/settings", new { creditControl = "Warn", quotationValidityDays = 30 });
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var order = await c.PostAsync<Order>("/api/sales/orders", new
        {
            customerId = customer, orderDate = Today, warehouseId = main, lines = new[] { new { itemId = service, quantity = 1m } }
        });
        order = await c.PostAsync<Order>($"/api/sales/orders/{order.Id}/confirm", new { });
        order.Status.ShouldBe("Confirmed");
        order.CreditWarning.ShouldNotBeNull();
    }

    [Fact]
    public async Task Alternative_units_convert_to_base_units_on_orders_and_quotations()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var bag = await PricedItemAsync(c, "BAG", "Stock", 20m);
        var pallet = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "PLT", nameEn = "Pallet", nameAr = "منصة" })).Id;
        var box = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "BOXX", nameEn = "Box", nameAr = "صندوق" })).Id;
        var units = await c.PutAsync<JsonElement>($"/api/inventory/items/{bag}/units", new[] { new { uomId = pallet, factor = 40m } });
        units.GetArrayLength().ShouldBe(2);   // base unit + pallet

        // Bought by the pallet: 2 pallets at 600 = 80 bags at 15.
        var supplier = (await c.PostAsync<JsonElement>("/api/purchasing/suppliers", new
        {
            code = "S-CEM", nameEn = "Cement works", country = "QA", isResident = true, hasPermanentEstablishment = false
        })).GetProperty("id").GetGuid();
        var po = await c.PostAsync<JsonElement>("/api/purchasing/orders", new
        {
            supplierId = supplier, orderDate = Today, warehouseId = main,
            lines = new[] { new { itemId = bag, quantity = 2m, unitPrice = 600m, uomId = pallet } }
        });
        var poLine = po.GetProperty("lines")[0];
        poLine.GetProperty("quantity").GetDecimal().ShouldBe(80m);
        poLine.GetProperty("unitPrice").GetDecimal().ShouldBe(15m);
        poLine.GetProperty("orderUomCode").GetString().ShouldBe("PLT");
        po.GetProperty("total").GetDecimal().ShouldBe(1_200m);

        // Quoted by the pallet at the item's price scaled to the unit (20 × 40 = 800), and the order keeps the unit.
        var customer = await CustomerAsync(c, "C-UNIT", null);
        var quote = await c.PostAsync<JsonElement>("/api/sales/quotations", new
        {
            customerId = customer, quoteDate = Today, lines = new[] { new { itemId = bag, quantity = 3m, uomId = pallet } }
        });
        var qLine = quote.GetProperty("lines")[0];
        qLine.GetProperty("uomPrice").GetDecimal().ShouldBe(800m);
        qLine.GetProperty("quantity").GetDecimal().ShouldBe(120m);
        quote.GetProperty("total").GetDecimal().ShouldBe(2_400m);
        var order = await c.PostAsync<JsonElement>($"/api/sales/quotations/{quote.GetProperty("id").GetGuid()}/convert", new { warehouseId = main });
        order.GetProperty("lines")[0].GetProperty("uomQuantity").GetDecimal().ShouldBe(3m);
        order.GetProperty("lines")[0].GetProperty("quantity").GetDecimal().ShouldBe(120m);
        order.GetProperty("total").GetDecimal().ShouldBe(2_400m);

        // A unit that is not set up for the item is refused.
        (await c.PostJsonAsync("/api/sales/quotations", new
        {
            customerId = customer, quoteDate = Today, lines = new[] { new { itemId = bag, quantity = 1m, uomId = box } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}

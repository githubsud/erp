using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class PurchasingTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record OrderLine(Guid Id, Guid ItemId, decimal Quantity, decimal QuantityReceived, decimal QuantityInvoiced);
    public sealed record Order(Guid Id, string PoNo, string Status, decimal Total, bool RequiresApproval, DateOnly? GtaNotificationDue, IReadOnlyList<OrderLine> Lines);
    public sealed record ReceiptLine(Guid Id, Guid ItemId, decimal Quantity, decimal Value);
    public sealed record Receipt(Guid Id, string GrnNo, string Status, decimal TotalValue, IReadOnlyList<ReceiptLine> Lines);
    public sealed record Invoice(Guid Id, string InvoiceNo, string Status, string MatchStatus, decimal Total, decimal WhtAmount, decimal OpenAmount);
    public sealed record Payment(Guid Id, string PaymentNo, decimal GrossAmount, decimal WhtAmount, decimal NetAmount, string Status);

    private static async Task<Guid> SupplierAsync(HttpClient c, string code, bool resident = true, string defaultWht = "None") =>
        (await c.PostAsync<JsonElement>("/api/purchasing/suppliers", new
        {
            code, nameEn = $"Supplier {code}", nameAr = "مورد", country = resident ? "QA" : "GB", isResident = resident,
            hasPermanentEstablishment = false, paymentTermsDays = 30, defaultWhtCategory = defaultWht
        })).GetProperty("id").GetGuid();

    private static async Task<Order> OrderAsync(HttpClient c, Guid supplier, Guid warehouse, params (Guid Item, decimal Qty, decimal Price)[] lines)
    {
        var order = await c.PostAsync<Order>("/api/purchasing/orders", new
        {
            supplierId = supplier, orderDate = Today, warehouseId = warehouse,
            lines = lines.Select(l => new { itemId = l.Item, quantity = l.Qty, unitPrice = l.Price }).ToArray()
        });
        return await c.PostAsync<Order>($"/api/purchasing/orders/{order.Id}/submit", new { });
    }

    private static Task<HttpResponseMessage> ReceiveAsync(HttpClient c, Order order, decimal quantity) =>
        c.PostJsonAsync("/api/purchasing/receipts", new
        {
            purchaseOrderId = order.Id, receiptDate = Today, lines = new[] { new { purchaseOrderLineId = order.Lines[0].Id, quantity } }
        });

    private static Task<Invoice> GoodsInvoiceAsync(HttpClient c, Guid supplier, string reference, Receipt receipt, decimal quantity, decimal price) =>
        c.PostAsync<Invoice>("/api/purchasing/invoices", new
        {
            supplierId = supplier, supplierRef = reference, invoiceDate = Today,
            lines = new[] { new { lineType = "Goods", goodsReceiptLineId = receipt.Lines[0].Id, quantity, unitPrice = price } }
        });

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Procure_to_pay_with_approval_three_way_match_and_landed_cost_reconciles_to_the_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var pipe = await InventoryTests.CreateItemAsync(c, "PIPE");
        var supplier = await SupplierAsync(c, "S-LOCAL");
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;

        // Under the 10,000 threshold an order is approved on submission; above it, it waits for approval.
        var po = await OrderAsync(c, supplier, main, (pipe, 10m, 100m));
        po.Status.ShouldBe("Approved");
        var big = await OrderAsync(c, supplier, main, (pipe, 200m, 100m));
        big.Status.ShouldBe("PendingApproval");
        (await ReceiveAsync(c, big, 1m)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.PostAsync<Order>($"/api/purchasing/orders/{big.Id}/approve", new { })).Status.ShouldBe("Approved");

        // Receive 6 of 10 at the order price: Dr Inventory 600 / Cr GRNI 600. Over-receipt is refused.
        var grn = await (await ReceiveAsync(c, po, 6m)).ReadAsync<Receipt>();
        grn.TotalValue.ShouldBe(600m);
        (await ReceiveAsync(c, po, 5m)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.GetAsync<Order>($"/api/purchasing/orders/{po.Id}")).Status.ShouldBe("PartiallyReceived");
        (await ClosingAsync(c, "2125")).ShouldBe(-600m);

        // Two units leave the store before the invoice arrives.
        await (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Damage", lines = new[] { new { itemId = pipe, quantity = -2m } }
        })).EnsureSuccessAsync();

        // Invoice at 101 (within the 2% tolerance): GRNI cleared at 600; the 6.00 difference is 4.00 to stock on hand, 2.00 to COGS.
        var invoice = await GoodsInvoiceAsync(c, supplier, "INV-1001", grn, 6m, 101m);
        invoice.MatchStatus.ShouldBe("Matched");
        invoice = await c.PostAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}/post", new { });
        invoice.Status.ShouldBe("Posted");
        (await ClosingAsync(c, "2125")).ShouldBe(0m);
        (await ClosingAsync(c, "2110")).ShouldBe(-606m);
        (await ClosingAsync(c, "5110")).ShouldBe(2m);
        (await ClosingAsync(c, "1150")).ShouldBe(404m);
        (await c.PostJsonAsync("/api/purchasing/invoices", new
        {
            supplierId = supplier, supplierRef = "inv-1001", invoiceDate = Today,
            lines = new[] { new { lineType = "Service", description = "Duplicate", quantity = 1m, unitPrice = 1m } }
        })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The rest at 110 (10% over): held until approved.
        var grn2 = await (await ReceiveAsync(c, po, 4m)).ReadAsync<Receipt>();
        (await c.GetAsync<Order>($"/api/purchasing/orders/{po.Id}")).Status.ShouldBe("Received");
        var held = await GoodsInvoiceAsync(c, supplier, "INV-1002", grn2, 4m, 110m);
        held.MatchStatus.ShouldBe("Variance");
        (await c.PostJsonAsync($"/api/purchasing/invoices/{held.Id}/post", new { })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await c.PostAsync<Invoice>($"/api/purchasing/invoices/{held.Id}/approve-variance", new { })).MatchStatus.ShouldBe("Approved");
        held = await c.PostAsync<Invoice>($"/api/purchasing/invoices/{held.Id}/post", new { });
        (await c.PostJsonAsync($"/api/purchasing/receipts/{grn2.Id}/reverse", new { date = Today, reason = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // already invoiced

        // Freight from a forwarder goes to the clearing account, then a landed cost voucher spreads it over both receipts by value.
        var forwarder = await SupplierAsync(c, "S-FWD");
        var freight = await c.PostAsync<Invoice>("/api/purchasing/invoices", new
        {
            supplierId = forwarder, supplierRef = "AWB-77", invoiceDate = Today,
            lines = new[] { new { lineType = "LandedCost", description = "Sea freight", quantity = 1m, unitPrice = 300m } }
        });
        freight = await c.PostAsync<Invoice>($"/api/purchasing/invoices/{freight.Id}/post", new { });
        (await ClosingAsync(c, "1190")).ShouldBe(300m);
        var lcv = await c.PostAsync<JsonElement>("/api/purchasing/landed-costs", new
        {
            date = Today, method = "Value", goodsReceiptIds = new[] { grn.Id, grn2.Id },
            charges = new[] { new { chargeType = "Freight", amount = 300m, supplierInvoiceId = freight.Id } }
        });
        lcv.GetProperty("allocations").EnumerateArray().Sum(a => a.GetProperty("amount").GetDecimal()).ShouldBe(300m);
        (await ClosingAsync(c, "1190")).ShouldBe(0m);
        (await c.PostJsonAsync("/api/purchasing/landed-costs", new
        {
            date = Today, method = "Quantity", goodsReceiptIds = new[] { grn.Id },
            charges = new[] { new { chargeType = "Freight", amount = 1m, supplierInvoiceId = freight.Id } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // freight invoice fully allocated

        // Stock valuation still equals the Inventory account; GRNI report equals its account.
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.Difference.ShouldBe(0m);
        var grni = await c.GetAsync<JsonElement>("/api/purchasing/reports/grni");
        grni.GetProperty("difference").GetDecimal().ShouldBe(0m);

        // Pay the first invoice in two parts; reversing a payment reopens the invoice.
        var part = await c.PostAsync<Payment>("/api/purchasing/payments", new
        {
            supplierId = supplier, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 300m } }
        });
        part.WhtAmount.ShouldBe(0m);
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).Status.ShouldBe("PartiallyPaid");
        (await c.PostJsonAsync("/api/purchasing/payments", new
        {
            supplierId = supplier, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 400m } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // only 306 open
        await c.PostAsync<Payment>($"/api/purchasing/payments/{part.Id}/reverse", new { date = Today, reason = "Wrong amount" });
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).OpenAmount.ShouldBe(606m);

        var aging = await c.GetAsync<JsonElement>($"/api/purchasing/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(aging.GetProperty("ledgerBalance").GetDecimal());
        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
    }

    [Fact]
    public async Task Non_resident_service_supplier_suffers_withholding_tax_on_payment()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var consulting = await InventoryTests.CreateItemAsync(c, "CONSULT", "Service");
        var foreign = await SupplierAsync(c, "S-UK", resident: false, defaultWht: "TechnicalFees");
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;

        // The contract with a non-resident must be notified to the GTA within 30 days of approval.
        var po = await OrderAsync(c, foreign, main, (consulting, 1m, 10_000m));
        po = await c.PostAsync<Order>($"/api/purchasing/orders/{po.Id}/approve", new { });
        po.GtaNotificationDue.ShouldNotBeNull();

        var invoice = await c.PostAsync<Invoice>("/api/purchasing/invoices", new
        {
            supplierId = foreign, supplierRef = "UK-55", invoiceDate = Today, purchaseOrderId = po.Id,
            lines = new[] { new { lineType = "Service", purchaseOrderLineId = po.Lines[0].Id, quantity = 1m, unitPrice = 10_000m } }
        });
        invoice.WhtAmount.ShouldBe(500m);   // 5% technical fees
        invoice = await c.PostAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}/post", new { });
        (await ClosingAsync(c, "5360")).ShouldBe(10_000m);

        var first = await c.PostAsync<Payment>("/api/purchasing/payments", new
        {
            supplierId = foreign, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 4_000m } }
        });
        first.WhtAmount.ShouldBe(200m);
        first.NetAmount.ShouldBe(3_800m);
        var last = await c.PostAsync<Payment>("/api/purchasing/payments", new
        {
            supplierId = foreign, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 6_000m } }
        });
        last.WhtAmount.ShouldBe(300m);
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).Status.ShouldBe("Paid");

        (await ClosingAsync(c, "2170")).ShouldBe(-500m);
        (await ClosingAsync(c, "2110")).ShouldBe(0m);
        (await ClosingAsync(c, "1120")).ShouldBe(-9_500m);
        var wht = await c.GetAsync<JsonElement>($"/api/purchasing/reports/wht?year={Today.Year}&month={Today.Month}");
        wht.GetProperty("totalTax").GetDecimal().ShouldBe(500m);
        wht.GetProperty("rows")[0].GetProperty("rate").GetDecimal().ShouldBe(5m);
    }

    [Fact]
    public async Task Debit_notes_return_goods_and_take_allowances_keeping_stock_equal_to_the_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var pipe = await InventoryTests.CreateItemAsync(c, "PIPE");
        var supplier = await SupplierAsync(c, "S-RET");
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;
        var po = await OrderAsync(c, supplier, main, (pipe, 10m, 100m));
        var grn = await (await ReceiveAsync(c, po, 10m)).ReadAsync<Receipt>();
        var invoice = await GoodsInvoiceAsync(c, supplier, "RET-1", grn, 10m, 100m);
        invoice = await c.PostAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}/post", new { });
        var lineId = (await c.GetAsync<JsonElement>($"/api/purchasing/invoices/{invoice.Id}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        await (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Damage", lines = new[] { new { itemId = pipe, quantity = -2m } }
        })).EnsureSuccessAsync();

        // Three pipes go back at the invoice price; stock leaves at average cost (100), so no difference.
        var ret = await c.PostAsync<JsonElement>("/api/purchasing/debit-notes", new
        {
            supplierInvoiceId = invoice.Id, date = Today, reason = "Wrong size",
            lines = new[] { new { lineType = "Return", supplierInvoiceLineId = lineId, quantity = 3m } }
        });
        ret.GetProperty("total").GetDecimal().ShouldBe(300m);
        ret.GetProperty("debitNoteNo").GetString()!.ShouldStartWith("PDN-");
        (await ClosingAsync(c, "2110")).ShouldBe(-700m);
        (await ClosingAsync(c, "1150")).ShouldBe(500m);

        // A price allowance of 80: half the goods are still on hand, so 40 lowers stock and 40 goes to cost of sales.
        await c.PostAsync<JsonElement>("/api/purchasing/debit-notes", new
        {
            supplierInvoiceId = invoice.Id, date = Today, reason = "Late delivery penalty",
            lines = new[] { new { lineType = "Allowance", supplierInvoiceLineId = lineId, quantity = 1m, unitPrice = 80m } }
        });
        (await ClosingAsync(c, "1150")).ShouldBe(460m);
        (await ClosingAsync(c, "5110")).ShouldBe(-40m);
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).OpenAmount.ShouldBe(620m);
        (await c.PostJsonAsync("/api/purchasing/debit-notes", new
        {
            supplierInvoiceId = invoice.Id, date = Today, reason = "Too much",
            lines = new[] { new { lineType = "Return", supplierInvoiceLineId = lineId, quantity = 8m } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // only 7 left to return

        await c.PostAsync<Payment>("/api/purchasing/payments", new
        {
            supplierId = supplier, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 620m } }
        });
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).Status.ShouldBe("Paid");
        (await c.PostJsonAsync($"/api/purchasing/invoices/{invoice.Id}/reverse", new { date = Today, reason = "x" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Reversing the return puts the goods back and the amount back on the invoice.
        await c.PostAsync<JsonElement>($"/api/purchasing/debit-notes/{ret.GetProperty("id").GetGuid()}/reverse", new { date = Today, reason = "Supplier refused" });
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).OpenAmount.ShouldBe(300m);
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.Difference.ShouldBe(0m);
        var aging = await c.GetAsync<JsonElement>($"/api/purchasing/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(aging.GetProperty("ledgerBalance").GetDecimal());
        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
    }

    [Fact]
    public async Task Euro_purchase_is_received_at_the_order_rate_and_paid_with_exchange_gains_and_losses()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, _, _) = await InventoryTests.DefaultsAsync(c);
        var tile = await InventoryTests.CreateItemAsync(c, "TILE");
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;
        var italy = (await c.PostAsync<JsonElement>("/api/purchasing/suppliers", new
        {
            code = "S-IT", nameEn = "Ceramiche", country = "IT", isResident = false, hasPermanentEstablishment = false, currency = "EUR"
        })).GetProperty("id").GetGuid();

        // 100 tiles at EUR 10 ordered at 4.00: received at QAR 40 each.
        var order = await c.PostAsync<JsonElement>("/api/purchasing/orders", new
        {
            supplierId = italy, orderDate = Today, warehouseId = main, exchangeRate = 4.00m,
            lines = new[] { new { itemId = tile, quantity = 100m, unitPrice = 10m } }
        });
        order.GetProperty("currency").GetString().ShouldBe("EUR");
        var po = await c.PostAsync<Order>($"/api/purchasing/orders/{order.GetProperty("id").GetGuid()}/submit", new { });
        var grn = await (await ReceiveAsync(c, po, 100m)).ReadAsync<Receipt>();
        grn.TotalValue.ShouldBe(4_000m);

        // Invoiced at 4.05: EUR 1,000 = QAR 4,050; the QAR 50 difference raises the stock value.
        var invoice = await c.PostAsync<Invoice>("/api/purchasing/invoices", new
        {
            supplierId = italy, supplierRef = "IT-77", invoiceDate = Today, exchangeRate = 4.05m,
            lines = new[] { new { lineType = "Goods", goodsReceiptLineId = grn.Lines[0].Id, quantity = 100m, unitPrice = 10m } }
        });
        invoice.MatchStatus.ShouldBe("Matched");
        invoice = await c.PostAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}/post", new { });
        (await ClosingAsync(c, "2110")).ShouldBe(-4_050m);
        (await ClosingAsync(c, "1150")).ShouldBe(4_050m);

        // EUR 600 paid at 4.10 (QAR 30 loss), the remaining EUR 400 at 3.95 (QAR 40 gain).
        var first = await c.PostAsync<JsonElement>("/api/purchasing/payments", new
        {
            supplierId = italy, date = Today, paymentAccountId = bank, exchangeRate = 4.10m,
            allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 600m } }
        });
        first.GetProperty("fxDifference").GetDecimal().ShouldBe(-30m);
        var second = await c.PostAsync<JsonElement>("/api/purchasing/payments", new
        {
            supplierId = italy, date = Today, paymentAccountId = bank, exchangeRate = 3.95m,
            allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 400m } }
        });
        second.GetProperty("fxDifference").GetDecimal().ShouldBe(40m);
        (await c.GetAsync<Invoice>($"/api/purchasing/invoices/{invoice.Id}")).Status.ShouldBe("Paid");
        (await ClosingAsync(c, "2110")).ShouldBe(0m);
        (await ClosingAsync(c, "1120")).ShouldBe(-4_040m);
        (await ClosingAsync(c, "4220")).ShouldBe(-10m);

        // A payment needs the rate; invoices in different currencies are paid separately.
        (await c.PostJsonAsync("/api/purchasing/payments", new
        {
            supplierId = italy, date = Today, paymentAccountId = bank, allocations = new[] { new { supplierInvoiceId = invoice.Id, amount = 1m } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.Difference.ShouldBe(0m);
        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
    }
}

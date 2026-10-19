using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SupplierCreditTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Aging(decimal Total, decimal LedgerBalance);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    private static async Task<JsonElement> InvoiceAsync(HttpClient c, Guid supplier, string reference, decimal amount)
    {
        var invoice = await c.PostAsync<JsonElement>("/api/purchasing/invoices", new
        {
            supplierId = supplier, supplierRef = reference, invoiceDate = Today,
            lines = new[] { new { lineType = "Service", description = "Consulting", quantity = 1m, unitPrice = amount } }
        });
        return await c.PostAsync<JsonElement>($"/api/purchasing/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
    }

    [Fact]
    public async Task Debit_notes_reduce_withholding_tax_and_leave_credits_on_paid_invoices()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;

        // Non-resident consultant: tax is withheld on payment. A 2,000 allowance before paying cuts the tax pro rata.
        var foreign = await PurchasingTests.SupplierAsync(c, "S-UK", resident: false, defaultWht: "TechnicalFees");
        var whtInvoice = await InvoiceAsync(c, foreign, "UK-1", 10_000m);
        var fullWht = whtInvoice.GetProperty("whtAmount").GetDecimal();
        fullWht.ShouldBeGreaterThan(0m);
        var lineId = whtInvoice.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var allowance = await c.PostAsync<JsonElement>("/api/purchasing/debit-notes", new
        {
            supplierInvoiceId = whtInvoice.GetProperty("id").GetGuid(), date = Today, reason = "Scope reduced",
            lines = new[] { new { lineType = "Allowance", supplierInvoiceLineId = lineId, quantity = 1m, unitPrice = 2_000m } }
        });
        allowance.GetProperty("whtReduction").GetDecimal().ShouldBe(fullWht / 5m);
        var payment = await c.PostAsync<JsonElement>("/api/purchasing/payments", new
        {
            supplierId = foreign, date = Today, paymentAccountId = bank,
            allocations = new[] { new { supplierInvoiceId = whtInvoice.GetProperty("id").GetGuid(), amount = 8_000m } }
        });
        payment.GetProperty("whtAmount").GetDecimal().ShouldBe(fullWht * 4m / 5m);
        (await (await c.PostJsonAsync($"/api/purchasing/debit-notes/{allowance.GetProperty("id").GetGuid()}/reverse", new { date = Today, reason = "x" }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.debit_note_wht");

        // Local supplier paid in full, then a 300 credit: the supplier owes it back.
        var local = await PurchasingTests.SupplierAsync(c, "S-QA");
        var paidInvoice = await InvoiceAsync(c, local, "QA-1", 1_000m);
        await c.PostAsync<JsonElement>("/api/purchasing/payments", new
        {
            supplierId = local, date = Today, paymentAccountId = bank,
            allocations = new[] { new { supplierInvoiceId = paidInvoice.GetProperty("id").GetGuid(), amount = 1_000m } }
        });
        var credit = await c.PostAsync<JsonElement>("/api/purchasing/debit-notes", new
        {
            supplierInvoiceId = paidInvoice.GetProperty("id").GetGuid(), date = Today, reason = "Overcharged",
            lines = new[] { new { lineType = "Allowance", description = "Refund of overcharge", quantity = 1m, unitPrice = 300m } }
        });
        var creditId = credit.GetProperty("id").GetGuid();
        credit.GetProperty("unappliedAmount").GetDecimal().ShouldBe(300m);
        (await ClosingAsync(c, "2110")).ShouldBe(300m); // the UK invoice is settled; the local supplier now owes us 300
        var aging = await c.GetAsync<Aging>($"/api/purchasing/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.Total.ShouldBe(aging.LedgerBalance);

        // 200 is set against the next invoice and 100 is refunded into the bank.
        var next = await InvoiceAsync(c, local, "QA-2", 500m);
        (await c.PostJsonAsync("/api/purchasing/credits/apply", new
        {
            debitNoteId = creditId, date = Today, allocations = new[] { new { supplierInvoiceId = next.GetProperty("id").GetGuid(), amount = 200m } }
        })).EnsureSuccessStatusCode();
        (await c.GetAsync<JsonElement>($"/api/purchasing/invoices/{next.GetProperty("id").GetGuid()}")).GetProperty("openAmount").GetDecimal().ShouldBe(300m);
        var refund = await c.PostAsync<JsonElement>("/api/purchasing/refunds", new { debitNoteId = creditId, date = Today, accountId = bank, amount = 100m });
        refund.GetProperty("status").GetString().ShouldBe("Posted");
        (await c.GetAsync<List<JsonElement>>($"/api/purchasing/credits?supplierId={local}")).ShouldBeEmpty();
        aging = await c.GetAsync<Aging>($"/api/purchasing/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.Total.ShouldBe(300m);
        aging.Total.ShouldBe(aging.LedgerBalance);
    }
}

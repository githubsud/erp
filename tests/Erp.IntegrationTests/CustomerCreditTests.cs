using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class CustomerCreditTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Credit(string SourceType, Guid SourceId, string DocumentNo, decimal Original, decimal Unapplied);
    public sealed record AgingRow(Guid CustomerId, decimal Total, decimal UnappliedCredit);
    public sealed record Aging(List<AgingRow> Rows, decimal Total, decimal LedgerBalance);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Receipts_on_account_and_credits_on_paid_invoices_are_applied_or_refunded_and_aging_matches_the_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (_, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var service = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "INSTALL", nameEn = "Installation", nameAr = "تركيب", itemType = "Service", itemGroupId = group, uomId = pcs, salesPrice = 500m
        })).Id;
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C1", nameEn = "Villa owner", nameAr = "مالك فيلا", paymentTermsDays = 30 }))
            .GetProperty("id").GetGuid();
        var cash = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1110").Id;

        async Task<SalesTests.Invoice> InvoiceAsync(decimal quantity)
        {
            var draft = await c.PostAsync<SalesTests.Invoice>("/api/sales/invoices", new
            {
                customerId = customer, invoiceDate = Today, lines = new[] { new { itemId = service, quantity } }
            });
            return await c.PostAsync<SalesTests.Invoice>($"/api/sales/invoices/{draft.Id}/post", new { });
        }

        // A deposit of 1,500 before anything is invoiced: all on account.
        var deposit = await c.PostAsync<JsonElement>("/api/sales/receipts", new
        {
            customerId = customer, date = Today, method = "Cash", depositAccountId = cash, allocations = Array.Empty<object>(), onAccount = 1_500m
        });
        deposit.GetProperty("unappliedAmount").GetDecimal().ShouldBe(1_500m);
        (await ClosingAsync(c, "1130")).ShouldBe(-1_500m);
        var aging = await c.GetAsync<Aging>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.Total.ShouldBe(aging.LedgerBalance);
        aging.Rows.Single().UnappliedCredit.ShouldBe(-1_500m);

        // The 1,000 invoice is settled from the deposit; 500 stays as credit.
        var first = await InvoiceAsync(2m);
        (await c.PostJsonAsync("/api/sales/credits/apply", new
        {
            sourceType = "Receipt", sourceId = deposit.GetProperty("id").GetGuid(), date = Today,
            allocations = new[] { new { salesInvoiceId = first.Id, amount = 1_000m } }
        })).EnsureSuccessStatusCode();
        (await c.GetAsync<SalesTests.Invoice>($"/api/sales/invoices/{first.Id}")).Status.ShouldBe("Paid");
        var credits = await c.GetAsync<List<Credit>>($"/api/sales/credits?customerId={customer}");
        credits.Single().Unapplied.ShouldBe(500m);
        (await (await c.PostJsonAsync("/api/sales/credits/apply", new
        {
            sourceType = "Receipt", sourceId = deposit.GetProperty("id").GetGuid(), date = Today,
            allocations = new[] { new { salesInvoiceId = first.Id, amount = 600m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.credit_exceeded");

        // A credit note of 300 on the paid invoice becomes customer credit instead of failing.
        var note = await c.PostAsync<JsonElement>("/api/sales/credit-notes", new
        {
            salesInvoiceId = first.Id, date = Today, reason = "Late installation",
            lines = new[] { new { lineType = "Allowance", description = "Delay discount", quantity = 1m, unitPrice = 300m } }
        });
        note.GetProperty("unappliedAmount").GetDecimal().ShouldBe(300m);
        (await c.GetAsync<List<Credit>>($"/api/sales/credits?customerId={customer}")).Sum(x => x.Unapplied).ShouldBe(800m);
        (await (await c.PostJsonAsync("/api/sales/credit-notes", new
        {
            salesInvoiceId = first.Id, date = Today, reason = "Too much",
            lines = new[] { new { lineType = "Allowance", description = "x", quantity = 1m, unitPrice = 800m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.credit_exceeds_invoice");

        // 200 of the credit note is refunded in cash.
        var refund = await c.PostAsync<JsonElement>("/api/sales/refunds", new
        {
            sourceType = "CreditNote", sourceId = note.GetProperty("id").GetGuid(), date = Today, accountId = cash, amount = 200m, reference = "Cash paid back"
        });
        refund.GetProperty("status").GetString().ShouldBe("Posted");
        (await ClosingAsync(c, "1110")).ShouldBe(1_300m);
        (await ClosingAsync(c, "1130")).ShouldBe(-600m);
        aging = await c.GetAsync<Aging>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.Total.ShouldBe(aging.LedgerBalance);
        aging.Total.ShouldBe(-600m);

        // Reversing the credit note needs the refund reversed first; then the credit disappears.
        var noteId = note.GetProperty("id").GetGuid();
        (await (await c.PostJsonAsync($"/api/sales/credit-notes/{noteId}/reverse", new { date = Today, reason = "Wrong" })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("sal.credit_refunded");
        await c.PostAsync<JsonElement>($"/api/sales/refunds/{refund.GetProperty("id").GetGuid()}/reverse", new { date = Today, reason = "Entered twice" });
        await c.PostAsync<JsonElement>($"/api/sales/credit-notes/{noteId}/reverse", new { date = Today, reason = "Wrong" });
        (await c.GetAsync<List<Credit>>($"/api/sales/credits?customerId={customer}")).Single().Unapplied.ShouldBe(500m);

        // Reversing the deposit takes its application back off the invoice.
        await c.PostAsync<JsonElement>($"/api/sales/receipts/{deposit.GetProperty("id").GetGuid()}/reverse", new { date = Today, reason = "Bounced transfer" });
        var reopened = await c.GetAsync<SalesTests.Invoice>($"/api/sales/invoices/{first.Id}");
        reopened.OpenAmount.ShouldBe(1_000m);
        aging = await c.GetAsync<Aging>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.Total.ShouldBe(aging.LedgerBalance);
        aging.Total.ShouldBe(1_000m);
    }
}

using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class FinanceCloseTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record CashFlowLine(string Code, decimal Amount);
    public sealed record CashFlowSection(string Kind, List<CashFlowLine> Lines, decimal Total);
    public sealed record CashFlow(decimal NetProfit, List<CashFlowSection> Sections, decimal NetChange, decimal OpeningCash, decimal ClosingCash, bool Reconciles);
    public sealed record ChecklistItem(string Code, bool Ok, bool Blocking);
    public sealed record Checklist(List<ChecklistItem> Items, bool CanClose);
    public sealed record Year(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status);
    public sealed record PdcRow(string ChequeNo, DateOnly ChequeDate, decimal Amount);
    public sealed record FxResult(Guid? Id, decimal TotalDifference, Guid? JournalEntryId);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code, DateOnly? at = null) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={(at ?? Today):yyyy-MM-dd}&to={(at ?? Today):yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    private static async Task<Guid> ServiceInvoiceAsync(HttpClient c, Guid supplier, string reference, decimal amount, string? currency = null,
        decimal? rate = null)
    {
        var invoice = await c.PostAsync<JsonElement>("/api/purchasing/invoices", new
        {
            supplierId = supplier, supplierRef = reference, invoiceDate = Today, currency, exchangeRate = rate,
            lines = new[] { new { lineType = "Service", description = "Maintenance", quantity = 1m, unitPrice = amount } }
        });
        var id = invoice.GetProperty("id").GetGuid();
        await c.PostAsync<JsonElement>($"/api/purchasing/invoices/{id}/post", new { });
        return id;
    }

    [Fact]
    public async Task Post_dated_cheque_to_a_supplier_waits_in_pdc_payable_until_it_clears()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var supplier = await PurchasingTests.SupplierAsync(c, "S1");
        var bank = (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;
        var invoice = await ServiceInvoiceAsync(c, supplier, "AC-1", 1_000m);

        var payment = await c.PostAsync<JsonElement>("/api/purchasing/payments", new
        {
            supplierId = supplier, date = Today, paymentAccountId = bank, chequeNo = "000451", chequeDate = Today.AddDays(10),
            allocations = new[] { new { supplierInvoiceId = invoice, amount = 1_000m } }
        });
        var paymentId = payment.GetProperty("id").GetGuid();
        payment.GetProperty("pdcStatus").GetString().ShouldBe("Issued");
        (await ClosingAsync(c, "2110")).ShouldBe(0m);
        (await ClosingAsync(c, "2120")).ShouldBe(-1_000m);
        (await ClosingAsync(c, "1120")).ShouldBe(0m);
        var register = await c.GetAsync<List<PdcRow>>("/api/purchasing/reports/pdc-issued");
        register.Single().Amount.ShouldBe(1_000m);

        (await (await c.PostJsonAsync($"/api/purchasing/payments/{paymentId}/clear", new { date = Today })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("pur.pdc_early");
        var cleared = await c.PostAsync<JsonElement>($"/api/purchasing/payments/{paymentId}/clear", new { date = Today.AddDays(10) });
        cleared.GetProperty("pdcStatus").GetString().ShouldBe("Cleared");
        var later = Today.AddDays(10);
        (await ClosingAsync(c, "2120", later)).ShouldBe(0m);
        (await ClosingAsync(c, "1120", later)).ShouldBe(-1_000m);
        (await c.GetAsync<List<PdcRow>>("/api/purchasing/reports/pdc-issued")).ShouldBeEmpty();

        // A cheque returned after clearing: both entries are reversed and the invoice is open again.
        await c.PostAsync<JsonElement>($"/api/purchasing/payments/{paymentId}/reverse", new { date = later, reason = "Cheque returned" });
        (await ClosingAsync(c, "1120", later)).ShouldBe(0m);
        (await ClosingAsync(c, "2110", later)).ShouldBe(-1_000m);
    }

    [Fact]
    public async Task Foreign_payables_are_revalued_at_month_end_and_the_entry_reverses_next_day()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var italy = (await c.PostAsync<JsonElement>("/api/purchasing/suppliers", new
        {
            code = "S-IT", nameEn = "Ceramiche", country = "IT", isResident = true, currency = "EUR"
        })).GetProperty("id").GetGuid();
        await ServiceInvoiceAsync(c, italy, "IT-1", 1_000m, "EUR", 4.00m);

        (await (await c.PostJsonAsync("/api/purchasing/fx-revaluations/preview", new { date = Today, rates = Array.Empty<object>() })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("pur.fx_rate_missing");
        var preview = await c.PostAsync<FxResult>("/api/purchasing/fx-revaluations/preview", new { date = Today, rates = new[] { new { currency = "EUR", rate = 4.10m } } });
        preview.TotalDifference.ShouldBe(100m);
        preview.Id.ShouldBeNull();

        var posted = await c.PostAsync<FxResult>("/api/purchasing/fx-revaluations", new { date = Today, rates = new[] { new { currency = "EUR", rate = 4.10m } } });
        posted.JournalEntryId.ShouldNotBeNull();
        // At month end the payable shows at the closing rate; the next day it is back at the booked 4,000.
        (await ClosingAsync(c, "2110")).ShouldBe(-4_100m);
        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.Rows.Single(r => r.Code == "4220").Debit.ShouldBe(100m);
        (await ClosingAsync(c, "2110", Today.AddDays(1))).ShouldBe(-4_000m);
    }

    [Fact]
    public async Task Cash_flow_reconciles_cost_centre_profit_and_loss_filters_and_a_year_can_be_closed()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var site = (await c.PostAsync<JsonElement>("/api/finance/cost-centers", new { code = "SITE-A", nameEn = "Site A", nameAr = "الموقع أ" }))
            .GetProperty("id").GetGuid();
        async Task Jv(params object[] lines) =>
            await c.PostAsync<FinanceTests.Voucher>("/api/finance/journal-vouchers", new { date = Today, memoEn = "Test", memoAr = "اختبار", lines });

        await Jv(new { accountId = A("1120"), debit = 10_000m, credit = 0m }, new { accountId = A("3110"), debit = 0m, credit = 10_000m });
        await Jv(new { accountId = A("5310"), debit = 1_000m, credit = 0m, costCenterId = site }, new { accountId = A("1120"), debit = 0m, credit = 1_000m });
        await Jv(new { accountId = A("5320"), debit = 200m, credit = 0m }, new { accountId = A("1110"), debit = 0m, credit = 200m });
        await Jv(new { accountId = A("1210"), debit = 3_000m, credit = 0m }, new { accountId = A("1120"), debit = 0m, credit = 3_000m });
        await Jv(new { accountId = A("5350"), debit = 500m, credit = 0m }, new { accountId = A("1220"), debit = 0m, credit = 500m });

        var flow = await c.GetAsync<CashFlow>($"/api/finance/reports/cash-flow?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        flow.NetProfit.ShouldBe(-1_700m);
        flow.Sections.Single(s => s.Kind == "operating").Total.ShouldBe(-1_200m); // loss, depreciation added back
        flow.Sections.Single(s => s.Kind == "investing").Total.ShouldBe(-3_000m);
        flow.Sections.Single(s => s.Kind == "financing").Total.ShouldBe(10_000m);
        flow.NetChange.ShouldBe(5_800m);
        flow.Reconciles.ShouldBeTrue();

        var pl = await c.GetAsync<JsonElement>($"/api/finance/reports/profit-and-loss?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&costCenterId={site}");
        pl.GetProperty("netProfit").GetDecimal().ShouldBe(-1_000m);

        // Close last year: the checklist passes, the year and its periods lock, and nothing can be posted into it.
        var years = await c.GetAsync<List<Year>>("/api/finance/fiscal-years");
        var current = years.Single(y => y.StartDate <= Today && y.EndDate >= Today);
        var previous = await c.PostAsync<Year>("/api/finance/fiscal-years", new { startDate = current.StartDate.AddYears(-1) });
        var checklist = await c.GetAsync<Checklist>($"/api/finance/fiscal-years/{previous.Id}/close-checklist");
        checklist.CanClose.ShouldBeTrue();
        (await c.PostAsync<Year>($"/api/finance/fiscal-years/{previous.Id}/close", new { })).Status.ShouldBe("Closed");
        var blocked = await c.PostJsonAsync("/api/finance/journal-vouchers", new
        {
            date = previous.EndDate, memoEn = "Late", lines = new object[]
            {
                new { accountId = A("5310"), debit = 10m, credit = 0m }, new { accountId = A("1110"), debit = 0m, credit = 10m }
            }
        });
        blocked.IsSuccessStatusCode.ShouldBeFalse();
        (await c.PostAsync<Year>($"/api/finance/fiscal-years/{previous.Id}/reopen", new { })).Status.ShouldBe("Open");
        (await c.GetAsync<Checklist>($"/api/finance/fiscal-years/{previous.Id}/close-checklist")).Items.ShouldContain(i => i.Code == "bank");
    }
}

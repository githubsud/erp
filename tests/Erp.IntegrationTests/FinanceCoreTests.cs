using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class FinanceCoreTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private static async Task<Dictionary<string, Guid>> AccountsAsync(HttpClient c) =>
        (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).ToDictionary(a => a.Code, a => a.Id);

    private static Task<JsonElement> VoucherAsync(HttpClient c, Dictionary<string, Guid> a, string debit, string credit, decimal amount, string memo) =>
        c.PostAsync<JsonElement>("/api/finance/journal-vouchers", new
        {
            date = Today, memoEn = memo, memoAr = memo,
            lines = new[]
            {
                new { accountId = a[debit], debit = amount, credit = 0m, description = memo },
                new { accountId = a[credit], debit = 0m, credit = amount, description = memo }
            }
        });

    [Fact]
    public async Task Income_statement_and_balance_sheet_follow_the_chart_and_balance()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var a = await AccountsAsync(c);
        await VoucherAsync(c, a, "1120", "3110", 100_000m, "Capital");
        await VoucherAsync(c, a, "1110", "4110", 5_000m, "Cash sales");
        await VoucherAsync(c, a, "5120", "1110", 1_000m, "Stock written off");
        await VoucherAsync(c, a, "5310", "1120", 2_000m, "Rent");

        var day = $"{Today:yyyy-MM-dd}";
        var pl = await c.GetAsync<JsonElement>($"/api/finance/reports/profit-and-loss?from={day}&to={day}&compareFrom={Today.AddDays(-1):yyyy-MM-dd}&compareTo={Today.AddDays(-1):yyyy-MM-dd}");
        pl.GetProperty("revenue").GetDecimal().ShouldBe(5_000m);
        pl.GetProperty("grossProfit").GetDecimal().ShouldBe(4_000m);
        pl.GetProperty("netProfit").GetDecimal().ShouldBe(2_000m);
        pl.GetProperty("compareNetProfit").GetDecimal().ShouldBe(0m);
        var kinds = pl.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("kind").GetString()).ToList();
        kinds.ShouldBe(["income", "cost_of_sales", "expense"]);

        var bs = await c.GetAsync<JsonElement>($"/api/finance/reports/balance-sheet?asOf={day}");
        bs.GetProperty("totalAssets").GetDecimal().ShouldBe(102_000m);
        bs.GetProperty("totalEquity").GetDecimal().ShouldBe(102_000m);
        bs.GetProperty("profitForYear").GetDecimal().ShouldBe(2_000m);
        bs.GetProperty("isBalanced").GetBoolean().ShouldBeTrue();

        (await c.GetAsync($"/api/finance/reports/profit-and-loss.pdf?from={day}&to={day}")).Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        (await c.GetAsync($"/api/finance/reports/balance-sheet.pdf?asOf={day}")).Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task Opening_invoices_bring_customer_and_supplier_balances_over_and_can_be_settled()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var a = await AccountsAsync(c);
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "OLD-C", nameEn = "Old customer", paymentTermsDays = 30 }))
            .GetProperty("id").GetGuid();
        var supplier = (await c.PostAsync<JsonElement>("/api/purchasing/suppliers", new
        {
            code = "OLD-S", nameEn = "Old supplier", country = "QA", isResident = true, hasPermanentEstablishment = false
        })).GetProperty("id").GetGuid();

        var invoices = await c.PostAsync<JsonElement>("/api/sales/opening-balances", new
        {
            postingDate = Today,
            invoices = new[]
            {
                new { customerId = customer, reference = "INV-0991", invoiceDate = Today.AddDays(-70), amount = 1_500m },
                new { customerId = customer, reference = "INV-1003", invoiceDate = Today.AddDays(-10), amount = 400m }
            }
        });
        invoices.GetArrayLength().ShouldBe(2);
        invoices[0].GetProperty("invoiceNo").GetString()!.ShouldStartWith("OBC-");
        await c.PostAsync<JsonElement>("/api/purchasing/opening-balances", new
        {
            postingDate = Today, invoices = new[] { new { supplierId = supplier, reference = "S-551", invoiceDate = Today.AddDays(-5), amount = 700m } }
        });

        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.Rows.Single(r => r.Code == "1130").Closing.ShouldBe(1_900m);
        tb.Rows.Single(r => r.Code == "2110").Closing.ShouldBe(-700m);
        tb.Rows.Single(r => r.Code == "3150").Closing.ShouldBe(-1_200m);
        var aging = await c.GetAsync<JsonElement>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(aging.GetProperty("ledgerBalance").GetDecimal());
        aging.GetProperty("rows")[0].GetProperty("days31To60").GetDecimal().ShouldBe(1_500m);

        // Collected like any invoice; opening invoices are not sales of the period.
        await c.PostAsync<JsonElement>("/api/sales/receipts", new
        {
            customerId = customer, date = Today, method = "BankTransfer", depositAccountId = a["1120"],
            allocations = new[] { new { salesInvoiceId = invoices[0].GetProperty("id").GetGuid(), amount = 1_500m } }
        });
        var analysis = await c.GetAsync<JsonElement>($"/api/sales/reports/analysis?from={Today.AddDays(-90):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        analysis.GetProperty("netRevenue").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task Bank_statement_import_matches_the_ledger_and_reconciles()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var a = await AccountsAsync(c);
        // Money banked before the first statement is covered by that statement's opening balance.
        var before = Today.DayOfYear > 1 ? 40_000m : 0m;
        if (before > 0)
            await (await c.PostJsonAsync("/api/finance/journal-vouchers", new
            {
                date = Today.AddDays(-1), memoEn = "Earlier deposit",
                lines = new[] { new { accountId = a["1120"], debit = before, credit = 0m }, new { accountId = a["3110"], debit = 0m, credit = before } }
            })).EnsureSuccessAsync();
        await VoucherAsync(c, a, "1120", "3110", 100_000m, "Capital deposited");
        await VoucherAsync(c, a, "5310", "1120", 2_000m, "Rent cheque 000451");
        await VoucherAsync(c, a, "5320", "1120", 300m, "Kahramaa - not yet presented");

        var d = Today.ToString("dd/MM/yyyy");
        string Bal(decimal x) => (before + x).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var csv = $"""
            QNB statement,,,,,
            Date,Description,Reference,Debit,Credit,Balance
            {d},Cash deposit capital,,,100000.00,{Bal(100_000m)}
            {d},Cheque 000451 cleared,000451,2000.00,,{Bal(98_000m)}
            {d},Bank charges,,25.00,,{Bal(97_975m)}
            """;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "qnb.csv");
        var import = await (await c.PostAsync($"/api/finance/bank-statements/import?accountId={a["1120"]}", form)).ReadAsync<JsonElement>();
        import.GetProperty("lines").GetInt32().ShouldBe(3);
        import.GetProperty("autoMatched").GetInt32().ShouldBe(2);
        var statementId = import.GetProperty("statementId").GetGuid();

        var statement = await c.GetAsync<JsonElement>($"/api/finance/bank-statements/{statementId}");
        statement.GetProperty("closingBalance").GetDecimal().ShouldBe(before + 97_975m);
        var charges = statement.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("status").GetString() == "Unmatched");

        var report = await c.GetAsync<JsonElement>($"/api/finance/reports/bank-reconciliation?accountId={a["1120"]}&asOf={Today:yyyy-MM-dd}");
        report.GetProperty("unclearedLedgerItems").GetArrayLength().ShouldBe(1);   // the Kahramaa cheque
        report.GetProperty("unrecordedBankItems").GetArrayLength().ShouldBe(1);    // the bank charges
        report.GetProperty("difference").GetDecimal().ShouldBe(0m);

        // Post the charges from the statement line; the statement is then reconciled.
        await (await c.PostJsonAsync($"/api/finance/bank-statements/lines/{charges.GetProperty("id").GetGuid()}/post",
            new { accountId = a["5340"], description = "Bank charges October" })).EnsureSuccessAsync();
        statement = await c.GetAsync<JsonElement>($"/api/finance/bank-statements/{statementId}");
        statement.GetProperty("status").GetString().ShouldBe("Reconciled");
        report = await c.GetAsync<JsonElement>($"/api/finance/reports/bank-reconciliation?accountId={a["1120"]}&asOf={Today:yyyy-MM-dd}");
        report.GetProperty("ledgerBalance").GetDecimal().ShouldBe(before + 97_675m);
        report.GetProperty("unrecordedBankItems").GetArrayLength().ShouldBe(0);
        report.GetProperty("difference").GetDecimal().ShouldBe(0m);

        // Manual match is checked against the amount; unmatching frees the ledger line again.
        var rent = statement.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("amount").GetDecimal() == -2_000m);
        var rentLine = rent.GetProperty("id").GetGuid();
        await (await c.PostJsonAsync($"/api/finance/bank-statements/lines/{rentLine}/unmatch", new { })).EnsureSuccessAsync();
        var candidates = await c.GetAsync<JsonElement>($"/api/finance/bank-statements/lines/{rentLine}/candidates");
        var wrong = candidates.EnumerateArray().First(x => x.GetProperty("amount").GetDecimal() == -300m).GetProperty("journalLineId").GetGuid();
        (await c.PostJsonAsync($"/api/finance/bank-statements/lines/{rentLine}/match", new { journalLineIds = new[] { wrong } }))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        var right = candidates[0].GetProperty("journalLineId").GetGuid();
        await (await c.PostJsonAsync($"/api/finance/bank-statements/lines/{rentLine}/match", new { journalLineIds = new[] { right } })).EnsureSuccessAsync();
    }
}

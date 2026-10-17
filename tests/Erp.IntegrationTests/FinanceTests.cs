using System.Net;
using Erp.IntegrationTests.Infrastructure;
using Npgsql;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class FinanceTests(ErpFixture fixture)
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    public sealed record Account(Guid Id, string Code, string NameEn, string NameAr, bool IsControl, bool IsPostable);
    public sealed record Voucher(Guid Id, string VoucherNo, string Status, Guid JournalEntryId, Guid? ReversalEntryId);
    public sealed record TbRow(string Code, decimal Opening, decimal Debit, decimal Credit, decimal Closing);
    public sealed record Tb(IReadOnlyList<TbRow> Rows, decimal TotalDebit, decimal TotalCredit, bool IsBalanced);
    public sealed record Period(Guid Id, int PeriodNo, DateOnly StartDate, DateOnly EndDate, string Status);
    public sealed record FiscalYear(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<Period> Periods);

    public static async Task<Voucher> PostSimpleVoucherAsync(HttpClient client, decimal amount)
    {
        var accounts = await client.GetAsync<List<Account>>("/api/finance/accounts");
        return await client.PostAsync<Voucher>("/api/finance/journal-vouchers", new
        {
            date = Today,
            memoEn = "Capital injection",
            memoAr = "إيداع رأس المال",
            lines = new object[]
            {
                new { accountId = accounts.Single(a => a.Code == "1120").Id, debit = amount, credit = 0m },
                new { accountId = accounts.Single(a => a.Code == "3110").Id, debit = 0m, credit = amount }
            }
        });
    }

    [Fact]
    public async Task Provisioning_seeds_a_bilingual_chart_of_accounts_and_current_fiscal_year()
    {
        var t = await fixture.CreateTenantAsync();
        var accounts = await t.Client.GetAsync<List<Account>>("/api/finance/accounts");
        accounts.Count.ShouldBeGreaterThan(50);
        accounts.ShouldAllBe(a => a.NameAr.Length > 0 && a.NameEn.Length > 0);
        accounts.Single(a => a.Code == "2210").NameAr.ShouldBe("مخصص مكافأة نهاية الخدمة");
        accounts.Single(a => a.Code == "1130").IsControl.ShouldBeTrue();

        var years = await t.Client.GetAsync<List<FiscalYear>>("/api/finance/fiscal-years");
        years.ShouldHaveSingleItem().Periods.Count.ShouldBe(12);
        years[0].StartDate.ShouldBeLessThanOrEqualTo(Today);
        years[0].EndDate.ShouldBeGreaterThanOrEqualTo(Today);
    }

    [Fact]
    public async Task Posted_voucher_appears_in_a_balanced_trial_balance()
    {
        var t = await fixture.CreateTenantAsync();
        var voucher = await PostSimpleVoucherAsync(t.Client, 250_000m);
        voucher.VoucherNo.ShouldBe($"JV-{Today.Year}-000001");

        var tb = await t.Client.GetAsync<Tb>($"/api/finance/reports/trial-balance?from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.TotalDebit.ShouldBe(250_000m);
        tb.Rows.Single(r => r.Code == "1120").Closing.ShouldBe(250_000m);
        tb.Rows.Single(r => r.Code == "3110").Closing.ShouldBe(-250_000m);
    }

    [Fact]
    public async Task Unbalanced_voucher_is_rejected_and_consumes_no_number()
    {
        var t = await fixture.CreateTenantAsync();
        var accounts = await t.Client.GetAsync<List<Account>>("/api/finance/accounts");
        var response = await t.Client.PostJsonAsync("/api/finance/journal-vouchers", new
        {
            date = Today,
            lines = new object[]
            {
                new { accountId = accounts.Single(a => a.Code == "1120").Id, debit = 100m, credit = 0m },
                new { accountId = accounts.Single(a => a.Code == "3110").Id, debit = 0m, credit = 99m }
            }
        });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ProblemAsync()).GetProperty("code").GetString().ShouldBe("fin.unbalanced");

        var next = await PostSimpleVoucherAsync(t.Client, 10m);
        next.VoucherNo.ShouldBe($"JV-{Today.Year}-000001");
    }

    [Fact]
    public async Task Manual_vouchers_cannot_post_to_control_accounts()
    {
        var t = await fixture.CreateTenantAsync();
        var accounts = await t.Client.GetAsync<List<Account>>("/api/finance/accounts");
        var response = await t.Client.PostJsonAsync("/api/finance/journal-vouchers", new
        {
            date = Today,
            lines = new object[]
            {
                new { accountId = accounts.Single(a => a.Code == "1130").Id, debit = 100m, credit = 0m },
                new { accountId = accounts.Single(a => a.Code == "4110").Id, debit = 0m, credit = 100m }
            }
        });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ProblemAsync()).GetProperty("code").GetString().ShouldBe("fin.control_account_manual");
    }

    [Fact]
    public async Task Reversal_mirrors_the_entry_once_and_only_once()
    {
        var t = await fixture.CreateTenantAsync();
        var voucher = await PostSimpleVoucherAsync(t.Client, 5_000m);

        var reversed = await t.Client.PostAsync<Voucher>($"/api/finance/journal-vouchers/{voucher.Id}/reverse", new { date = Today, reason = "Entered twice" });
        reversed.Status.ShouldBe("Reversed");
        reversed.ReversalEntryId.ShouldNotBeNull();

        var tb = await t.Client.GetAsync<Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.Rows.Single(r => r.Code == "1120").Closing.ShouldBe(0m);

        var again = await t.Client.PostJsonAsync($"/api/finance/journal-vouchers/{voucher.Id}/reverse", new { date = Today, reason = "again" });
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Ledger_rows_cannot_be_updated_or_deleted()
    {
        var t = await fixture.CreateTenantAsync();
        await PostSimpleVoucherAsync(t.Client, 100m);

        await using var app = await fixture.OpenAppConnectionAsync(t.TenantId);
        var update = new NpgsqlCommand("UPDATE fin.journal_line SET debit_base = debit_base + 1", app);
        (await Should.ThrowAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState.ShouldBe("42501");
        var delete = new NpgsqlCommand("DELETE FROM fin.journal_entry", app);
        (await Should.ThrowAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState.ShouldBe("42501");

        // Even the owner role is stopped by the immutability trigger.
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        var ownerUpdate = new NpgsqlCommand($"UPDATE fin.journal_line SET description = 'x' WHERE tenant_id = '{t.TenantId}'", owner);
        (await Should.ThrowAsync<PostgresException>(() => ownerUpdate.ExecuteNonQueryAsync())).SqlState.ShouldBe("P0001");
    }

    [Fact]
    public async Task Database_rejects_an_unbalanced_entry_at_commit()
    {
        var t = await fixture.CreateTenantAsync();
        await using var app = await fixture.OpenAppConnectionAsync(t.TenantId);
        var periodId = (Guid)(await new NpgsqlCommand(
            $"SELECT id FROM fin.fiscal_period WHERE start_date <= '{Today:yyyy-MM-dd}' AND end_date >= '{Today:yyyy-MM-dd}'", app).ExecuteScalarAsync())!;
        var accountId = (Guid)(await new NpgsqlCommand("SELECT id FROM fin.account WHERE code = '1120'", app).ExecuteScalarAsync())!;

        await using var tx = await app.BeginTransactionAsync();
        var entryId = Guid.NewGuid();
        await new NpgsqlCommand($"""
            INSERT INTO fin.journal_entry (id, tenant_id, company_id, entry_no, posting_date, fiscal_period_id, source_type, source_id, source_no, posted_by, posted_at, created_by)
            VALUES ('{entryId}', '{t.TenantId}', '{t.CompanyId}', 'HACK-1', '{Today:yyyy-MM-dd}', '{periodId}', 'hack', gen_random_uuid(), 'H1', gen_random_uuid(), now(), gen_random_uuid());
            INSERT INTO fin.journal_line (id, tenant_id, journal_entry_id, line_no, account_id, currency, fx_rate, debit_txn, debit_base, created_by)
            VALUES (gen_random_uuid(), '{t.TenantId}', '{entryId}', 1, '{accountId}', 'QAR', 1, 50, 50, gen_random_uuid());
            """, app, tx).ExecuteNonQueryAsync();
        (await Should.ThrowAsync<PostgresException>(() => tx.CommitAsync())).MessageText.ShouldContain("fewer than two lines");
    }

    [Fact]
    public async Task Closed_periods_reject_postings()
    {
        var t = await fixture.CreateTenantAsync();
        var year = (await t.Client.GetAsync<List<FiscalYear>>("/api/finance/fiscal-years")).Single();
        var current = year.Periods.Single(p => p.StartDate <= Today && p.EndDate >= Today);

        (await t.Client.PostAsync($"/api/finance/periods/{current.Id}/close", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var accounts = await t.Client.GetAsync<List<Account>>("/api/finance/accounts");
        var response = await t.Client.PostJsonAsync("/api/finance/journal-vouchers", new
        {
            date = Today,
            lines = new object[]
            {
                new { accountId = accounts.Single(a => a.Code == "1120").Id, debit = 1m, credit = 0m },
                new { accountId = accounts.Single(a => a.Code == "3110").Id, debit = 0m, credit = 1m }
            }
        });
        (await response.ProblemAsync()).GetProperty("code").GetString().ShouldBe("fin.period_closed");

        (await t.Client.PostAsync($"/api/finance/periods/{current.Id}/reopen", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await PostSimpleVoucherAsync(t.Client, 1m);
    }

    [Fact]
    public async Task Concurrent_postings_get_gapless_unique_numbers()
    {
        var t = await fixture.CreateTenantAsync();
        await PostSimpleVoucherAsync(t.Client, 1m); // warm up sequences

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostSimpleVoucherAsync(t.Client, 1m)));
        var numbers = results.Select(r => int.Parse(r.VoucherNo[^6..])).Order().ToList();
        numbers.ShouldBe(Enumerable.Range(2, 20).ToList());
    }

    [Fact]
    public async Task Journal_export_is_excel_friendly_utf8_with_arabic()
    {
        var t = await fixture.CreateTenantAsync();
        await PostSimpleVoucherAsync(t.Client, 777m);
        var bytes = await t.Client.GetByteArrayAsync($"/api/finance/exports/journal.csv?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.ShouldContain("رأس المال");
        text.ShouldContain("777.00");
    }
}

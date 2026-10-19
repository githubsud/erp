using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class BudgetTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Year(Guid Id, DateOnly StartDate, DateOnly EndDate);
    public sealed record Budget(Guid Id, string Status, decimal TotalIncome, decimal TotalExpense);
    public sealed record VarianceRow(string Code, decimal Budget, decimal Actual, decimal Variance, bool Favourable);
    public sealed record Variance(List<VarianceRow> Rows, decimal BudgetProfit, decimal ActualProfit);

    [Fact]
    public async Task Approved_budget_is_compared_with_actuals_for_the_chosen_months()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var year = (await c.GetAsync<List<Year>>("/api/finance/fiscal-years")).Single(y => y.StartDate <= Today && y.EndDate >= Today);
        var period = (Today.Year - year.StartDate.Year) * 12 + Today.Month - year.StartDate.Month + 1;

        var budget = await c.PostAsync<Budget>("/api/finance/budgets", new { fiscalYearId = year.Id, name = "Plan" });
        decimal[] Month(decimal amount) => Enumerable.Range(1, 12).Select(p => p == period ? amount : 0m).ToArray();
        budget = await c.PutAsync<Budget>($"/api/finance/budgets/{budget.Id}/lines", new object[]
        {
            new { accountId = A("4120"), months = Month(10_000m) },
            new { accountId = A("5310"), months = Month(3_000m) }
        });
        budget.TotalIncome.ShouldBe(10_000m);
        budget.TotalExpense.ShouldBe(3_000m);
        (await (await c.PutAsJsonAsync($"/api/finance/budgets/{budget.Id}/lines", new object[] { new { accountId = A("1120"), months = Month(1m) } }, ErpFixture.Json))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("fin.budget_account");
        (await c.PostAsync<Budget>($"/api/finance/budgets/{budget.Id}/approve", new { })).Status.ShouldBe("Approved");
        (await c.PutAsJsonAsync($"/api/finance/budgets/{budget.Id}/lines", Array.Empty<object>(), ErpFixture.Json)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Actual: service revenue 8,000 and rent 3,500 this month.
        await c.PostAsync<FinanceTests.Voucher>("/api/finance/journal-vouchers", new
        {
            date = Today, memoEn = "Revenue", lines = new object[]
            {
                new { accountId = A("1110"), debit = 8_000m, credit = 0m }, new { accountId = A("4120"), debit = 0m, credit = 8_000m }
            }
        });
        await c.PostAsync<FinanceTests.Voucher>("/api/finance/journal-vouchers", new
        {
            date = Today, memoEn = "Rent", lines = new object[]
            {
                new { accountId = A("5310"), debit = 3_500m, credit = 0m }, new { accountId = A("1110"), debit = 0m, credit = 3_500m }
            }
        });

        var variance = await c.GetAsync<Variance>($"/api/finance/budgets/{budget.Id}/variance?fromPeriod={period}&toPeriod={period}");
        var revenue = variance.Rows.Single(r => r.Code == "4120");
        revenue.Actual.ShouldBe(8_000m);
        revenue.Variance.ShouldBe(-2_000m);
        revenue.Favourable.ShouldBeFalse();
        var rent = variance.Rows.Single(r => r.Code == "5310");
        rent.Variance.ShouldBe(500m);
        rent.Favourable.ShouldBeFalse();
        variance.BudgetProfit.ShouldBe(7_000m);
        variance.ActualProfit.ShouldBe(4_500m);
    }
}

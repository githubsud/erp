using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class FixedAssetTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;
    private static readonly DateOnly MonthStart = new(Today.Year, Today.Month, 1);

    public sealed record Asset(Guid Id, string AssetNo, string Status, decimal Cost, decimal MonthlyDepreciation, decimal AccumulatedDepreciation, decimal NetBookValue);
    public sealed record Register(List<Asset> Assets, decimal Cost, decimal Accumulated, decimal NetBookValue, decimal LedgerCost, decimal LedgerAccumulated);
    public sealed record Run(Guid Id, string RunNo, decimal Total, int AssetCount);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code, DateOnly at) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={at:yyyy-MM-dd}&to={at:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Assets_are_capitalised_depreciated_monthly_and_disposed_with_the_register_matching_the_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var vehicles = (await c.PostAsync<JsonElement>("/api/finance/assets/categories", new
        {
            code = "VEH", nameEn = "Vehicles", nameAr = "مركبات", usefulLifeMonths = 36, assetAccountId = A("1210"), accumulatedAccountId = A("1220"),
            expenseAccountId = A("5350")
        })).GetProperty("id").GetGuid();

        // A pickup bought for 36,000 and a used van brought over with 6,000 already depreciated.
        var pickup = await c.PostAsync<Asset>("/api/finance/assets", new
        {
            nameEn = "Toyota Hilux", nameAr = "تويوتا هايلكس", categoryId = vehicles, acquisitionDate = MonthStart, cost = 36_000m, salvageValue = 0m
        });
        pickup.MonthlyDepreciation.ShouldBe(1_000m);
        var van = await c.PostAsync<Asset>("/api/finance/assets", new
        {
            nameEn = "Delivery van", nameAr = "فان توصيل", categoryId = vehicles, acquisitionDate = MonthStart, cost = 24_000m, salvageValue = 0m,
            usefulLifeMonths = 24, openingDepreciation = 6_000m
        });
        await c.PostAsync<Asset>($"/api/finance/assets/{pickup.Id}/capitalize", new { date = MonthStart, offsetAccountId = A("1120") });
        await c.PostAsync<Asset>($"/api/finance/assets/{van.Id}/capitalize", new { date = MonthStart, offsetAccountId = A("3150") });
        (await ClosingAsync(c, "1210", MonthStart)).ShouldBe(60_000m);
        (await ClosingAsync(c, "1220", MonthStart)).ShouldBe(-6_000m);

        // One month: 1,000 + 1,000; running the same month again is refused.
        var run = await c.PostAsync<Run>("/api/finance/assets/depreciation-runs", new { year = Today.Year, month = Today.Month });
        run.Total.ShouldBe(2_000m);
        run.AssetCount.ShouldBe(2);
        (await c.PostJsonAsync("/api/finance/assets/depreciation-runs", new { year = Today.Year, month = Today.Month })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var monthEnd = MonthStart.AddMonths(1).AddDays(-1);
        (await ClosingAsync(c, "5350", monthEnd)).ShouldBe(2_000m);

        var register = await c.GetAsync<Register>("/api/finance/assets");
        register.Cost.ShouldBe(register.LedgerCost);
        register.Accumulated.ShouldBe(register.LedgerAccumulated);
        register.NetBookValue.ShouldBe(52_000m);

        // The pickup is sold for 36,000: net book value 35,000, so a 1,000 gain.
        var sold = await c.PostAsync<Asset>($"/api/finance/assets/{pickup.Id}/dispose", new
        {
            date = monthEnd, proceeds = 36_000m, proceedsAccountId = A("1120"), reason = "Sold to a dealer"
        });
        sold.Status.ShouldBe("Disposed");
        (await ClosingAsync(c, "4230", monthEnd)).ShouldBe(-1_000m);
        (await ClosingAsync(c, "1120", monthEnd)).ShouldBe(0m);
        register = await c.GetAsync<Register>("/api/finance/assets");
        register.Cost.ShouldBe(register.LedgerCost);
        register.Accumulated.ShouldBe(register.LedgerAccumulated);

        // A run cannot be reversed once one of its assets was disposed of.
        var reverse = await c.PostJsonAsync($"/api/finance/assets/depreciation-runs/{run.Id}/reverse", new { date = monthEnd, reason = "Wrong" });
        (await reverse.ProblemAsync()).GetProperty("code").GetString().ShouldBe("fin.depreciation_disposed");
    }
}

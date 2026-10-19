using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class HrReportTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;
    private static readonly DateOnly MonthStart = new(Today.Year, Today.Month, 1);
    private static readonly DateOnly MonthEnd = MonthStart.AddMonths(1).AddDays(-1);

    public sealed record RegisterRow(string EmpNo, Dictionary<string, decimal> Amounts, decimal Gross, decimal Net);
    public sealed record Register(List<RegisterRow> Rows, Dictionary<string, decimal> Totals, decimal Gross, decimal Net);
    public sealed record CountRow(string Key, int Count);
    public sealed record Headcount(int Total, int Qatari, List<CountRow> ByNationality);
    public sealed record Leave(decimal TotalProvision, decimal LedgerBalance, List<JsonElement> Rows);
    public sealed record Wps(string RunNo, decimal WpsNet, int WpsEmployees, decimal? SifTotal, bool Matches, List<string> Issues);

    [Fact]
    public async Task Payroll_register_headcount_leave_liability_and_wps_reconciliation_agree_with_payroll_and_ledger()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await PayrollTests.SetUpWorkforceAsync(c);
        var run = await c.PostAsync<PayrollTests.Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        foreach (var action in new[] { "calculate", "approve", "post" })
            run = await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/{action}", new { });

        var register = await c.GetAsync<Register>($"/api/hr/reports/payroll-register?runId={run.Id}");
        register.Rows.Count.ShouldBe(3);
        register.Net.ShouldBe(run.TotalNet);
        register.Gross.ShouldBe(run.TotalGross);
        register.Totals["BASIC"].ShouldBe(register.Rows.Sum(r => r.Amounts.GetValueOrDefault("BASIC")));
        var excel = await c.GetAsync($"/api/hr/reports/payroll-register.xlsx?runId={run.Id}");
        excel.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await excel.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(2_000);

        var headcount = await c.GetAsync<Headcount>($"/api/hr/reports/headcount?asOf={MonthEnd:yyyy-MM-dd}");
        headcount.Total.ShouldBe(3);
        headcount.Qatari.ShouldBe(1);
        headcount.ByNationality.Select(n => n.Key).ShouldBe(["IN", "PH", "QA"], ignoreOrder: true);

        var leave = await c.GetAsync<Leave>($"/api/hr/reports/leave-liability?asOf={MonthEnd:yyyy-MM-dd}");
        leave.Rows.Count.ShouldBe(3);
        leave.TotalProvision.ShouldBe(leave.LedgerBalance);

        var before = (await c.GetAsync<List<Wps>>($"/api/hr/reports/wps-reconciliation?year={MonthStart.Year}")).Single();
        before.Issues.ShouldContain("no_sif");
        await c.PostAsync<JsonElement>($"/api/hr/payroll-runs/{run.Id}/wps-files", new { });
        var after = (await c.GetAsync<List<Wps>>($"/api/hr/reports/wps-reconciliation?year={MonthStart.Year}")).Single();
        after.SifTotal.ShouldBe(after.WpsNet);
        after.WpsEmployees.ShouldBe(3);
        after.Issues.ShouldNotContain("no_sif");
        after.Issues.ShouldNotContain("sif_total");
    }
}

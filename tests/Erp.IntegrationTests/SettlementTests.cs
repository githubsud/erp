using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SettlementTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;
    private static readonly DateOnly MonthStart = new(Today.Year, Today.Month, 1);
    private static readonly DateOnly MonthEnd = MonthStart.AddMonths(1).AddDays(-1);

    public sealed record Line(string Code, string Kind, decimal Amount);
    public sealed record Settlement(Guid Id, string SettlementNo, string Status, int ServiceDays, bool EligibleForGratuity, decimal GratuityAmount,
        decimal GratuityProvision, decimal LeaveBalanceDays, decimal UnpaidSalaryDays, decimal TotalEarnings, decimal TotalDeductions,
        decimal NetPayable, IReadOnlyList<Line> Lines);

    private static async Task<Guid> HireWithContractAsync(HttpClient c, string empNo, DateOnly joining, string qid, string account,
        decimal? annualAirTicket = null)
    {
        var e = await c.PostAsync<PayrollTests.Employee>("/api/hr/employees", new
        {
            empNo, nameEn = $"Employee {empNo}", nameAr = "موظف", nationality = "IN", gender = "Male", joiningDate = joining, qid,
            iban = PayrollTests.Iban("CBQA", account)
        });
        await c.PostAsync<JsonElement>($"/api/hr/employees/{e.Id}/contracts", new
        {
            startDate = joining, contractType = "Unlimited", probationMonths = 0, accommodationProvided = false, foodProvided = true,
            gratuityBasis = "Basic",
            annualAirTicketAmount = annualAirTicket,
            components = new[] { new { componentCode = "BASIC", amount = 3000m }, new { componentCode = "HOUSING", amount = 1000m } }
        });
        return e.Id;
    }

    private static async Task<Guid> BankAsync(HttpClient c) =>
        (await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts")).Single(a => a.Code == "1120").Id;

    [Fact]
    public async Task Settlement_after_payroll_releases_gratuity_provision_and_recovers_advances()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var bank = await BankAsync(c);
        var employeeId = await HireWithContractAsync(c, "S001", new DateOnly(2024, 3, 1), "28435611111", "000000000001010101010", annualAirTicket: 2400m);

        // Salary advance of 2,000 paid from the bank.
        var advance = await c.PostAsync<JsonElement>($"/api/hr/employees/{employeeId}/advances",
            new { date = MonthStart, amount = 2000m, paymentAccountId = bank, monthlyRecovery = 500m });
        advance.GetProperty("advanceNo").GetString()!.ShouldStartWith("ADV-");
        (await c.GetAsync<JsonElement>($"/api/hr/employees/{employeeId}/advance-balance?asOf={MonthEnd:yyyy-MM-dd}"))
            .GetProperty("outstanding").GetDecimal().ShouldBe(2000m);

        // Month's payroll posted and paid: gratuity provision now equals the liability at month end.
        var run = await c.PostAsync<PayrollTests.Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/approve", new { });
        run = await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/post", new { });
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/mark-paid", new { paymentAccountId = bank, paymentDate = MonthEnd });
        var provisioned = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        provisioned.Rows.Single(r => r.Code == "2160").Closing.ShouldBe(-200m);   // 2,400 / 12 air ticket provision
        provisioned.Rows.Single(r => r.Code == "2150").Closing.ShouldBeLessThan(0m); // leave salary provision

        var settlement = await c.PostAsync<Settlement>($"/api/hr/employees/{employeeId}/final-settlements",
            new { lastWorkingDay = MonthEnd, reason = "Resignation", noticePay = 500m, airTicket = 1200m });
        settlement.Status.ShouldBe("Draft");
        settlement.EligibleForGratuity.ShouldBeTrue();
        settlement.UnpaidSalaryDays.ShouldBe(0);                       // salary already paid by the month's payroll
        settlement.GratuityProvision.ShouldBe(settlement.GratuityAmount); // fully provided by monthly accruals
        settlement.Lines.Single(l => l.Code == "ADVANCE_RECOVERY").Amount.ShouldBe(2000m);
        settlement.Lines.Single(l => l.Code == "NOTICE_PAY").Amount.ShouldBe(500m);
        settlement.NetPayable.ShouldBe(settlement.TotalEarnings - settlement.TotalDeductions);

        var pdf = await c.GetAsync($"/api/hr/final-settlements/{settlement.Id}/pdf");
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        var pdfBytes = await pdf.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5).ShouldBe("%PDF-");

        settlement = await c.PostAsync<Settlement>($"/api/hr/final-settlements/{settlement.Id}/post", new { });
        settlement.Status.ShouldBe("Posted");

        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.Rows.Single(r => r.Code == "2210").Closing.ShouldBe(0m);      // provision released
        tb.Rows.Single(r => r.Code == "1160").Closing.ShouldBe(0m);      // advance recovered
        tb.Rows.Single(r => r.Code == "2150").Closing.ShouldBe(0m);      // leave provision released
        tb.Rows.Single(r => r.Code == "2160").Closing.ShouldBe(0m);      // ticket provision released
        tb.Rows.Single(r => r.Code == "5290").Closing.ShouldBe(1200m);   // ticket cost = amount paid (200 accrued, released, 1,200 expensed)
        tb.Rows.Single(r => r.Code == "2140").Closing.ShouldBe(-settlement.NetPayable);

        var employee = await c.GetAsync<JsonElement>($"/api/hr/employees/{employeeId}");
        employee.GetProperty("status").GetString().ShouldBe("Terminated");

        await c.PostAsync<Settlement>($"/api/hr/final-settlements/{settlement.Id}/mark-paid", new { paymentAccountId = bank, paymentDate = MonthEnd });
        tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        tb.Rows.Single(r => r.Code == "2140").Closing.ShouldBe(0m);

        // A terminated, settled employee is not paid again by a later payroll for the same month.
        var second = await c.PostJsonAsync($"/api/hr/employees/{employeeId}/final-settlements", new { lastWorkingDay = MonthEnd, reason = "Resignation" });
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Short_service_settlement_pays_salary_and_leave_but_no_gratuity_and_can_be_reversed()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var employeeId = await HireWithContractAsync(c, "S002", MonthStart, "28435622222", "000000000002020202020");
        var lastDay = MonthStart.AddDays(9);

        var settlement = await c.PostAsync<Settlement>($"/api/hr/employees/{employeeId}/final-settlements",
            new { lastWorkingDay = lastDay, reason = "Termination" });
        settlement.EligibleForGratuity.ShouldBeFalse();
        settlement.GratuityAmount.ShouldBe(0m);
        settlement.UnpaidSalaryDays.ShouldBe(10);
        settlement.Lines.Single(l => l.Code == "SALARY_BASIC").Amount.ShouldBe(1000m);   // 3000 × 10/30
        settlement.Lines.Single(l => l.Code == "SALARY_HOUSING").Amount.ShouldBe(333.33m);

        // Drafts can be discarded and recreated.
        (await c.DeleteAsync($"/api/hr/final-settlements/{settlement.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        settlement = await c.PostAsync<Settlement>($"/api/hr/employees/{employeeId}/final-settlements",
            new { lastWorkingDay = lastDay, reason = "Termination", otherDeductions = 100m });
        settlement = await c.PostAsync<Settlement>($"/api/hr/final-settlements/{settlement.Id}/post", new { });

        var reversed = await c.PostAsync<Settlement>($"/api/hr/final-settlements/{settlement.Id}/reverse", new { date = lastDay, reason = "Employee withdrew resignation" });
        reversed.Status.ShouldBe("Reversed");
        (await c.GetAsync<JsonElement>($"/api/hr/employees/{employeeId}")).GetProperty("status").GetString().ShouldBe("Active");
        var contracts = await c.GetAsync<List<JsonElement>>($"/api/hr/employees/{employeeId}/contracts");
        contracts.Single().GetProperty("status").GetString().ShouldBe("Active");

        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.Rows.Where(r => r.Code is "2140" or "5210").ShouldAllBe(r => r.Closing == 0m);
    }

    [Fact]
    public async Task Payslip_pdfs_are_generated_with_the_arabic_font()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await HireWithContractAsync(c, "P001", new DateOnly(2025, 1, 1), "28435633333", "000000000003030303030");
        await HireWithContractAsync(c, "P002", new DateOnly(2025, 6, 1), "28435644444", "000000000004040404040");
        var run = await c.PostAsync<PayrollTests.Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        var detail = await c.GetAsync<PayrollTests.RunDetail>($"/api/hr/payroll-runs/{run.Id}");

        var single = await c.GetAsync($"/api/hr/payroll-runs/{run.Id}/payslips/{detail.Payslips[0].Id}/pdf");
        single.StatusCode.ShouldBe(HttpStatusCode.OK);
        single.Content.Headers.ContentDisposition!.FileName!.ShouldContain("P001");
        var bytes = await single.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
        System.Text.Encoding.Latin1.GetString(bytes).ShouldContain("IBMPlexSansArabic");

        var all = await c.GetByteArrayAsync($"/api/hr/payroll-runs/{run.Id}/payslips.pdf");
        all.Length.ShouldBeGreaterThan(bytes.Length);
    }
}

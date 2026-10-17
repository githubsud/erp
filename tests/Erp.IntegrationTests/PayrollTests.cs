using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class PayrollTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;
    private static readonly DateOnly MonthStart = new(Today.Year, Today.Month, 1);
    private static readonly DateOnly MonthEnd = MonthStart.AddMonths(1).AddDays(-1);

    public sealed record Employee(Guid Id, string EmpNo, string NameEn, string? Qid, string? Iban, bool SensitiveMasked);
    public sealed record Run(Guid Id, string RunNo, string Status, int EmployeeCount, decimal TotalGross, decimal TotalDeductions,
        decimal TotalNet, decimal TotalGratuityAccrual, DateOnly DueDate, DateOnly WpsDeadline, Guid? JournalEntryId, Guid? GratuityJournalEntryId);
    public sealed record Payslip(Guid Id, Guid EmployeeId, string EmpNo, decimal PaidDays, decimal UnpaidDays, decimal Gross, decimal Deductions, decimal Net,
        decimal HalfPayDays = 0);
    public sealed record RunDetail(Run Run, IReadOnlyList<Payslip> Payslips);
    public sealed record WpsFile(Guid Id, string FileName, decimal TotalSalaries, int RecordCount);
    public sealed record GratuityRow(Guid EmployeeId, string EmpNo, decimal Liability, decimal Provision);

    public static string Iban(string bank, string account)
    {
        var bban = bank + account;
        var numeric = string.Concat((bban + "QA00").Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        return $"QA{98 - (int)(BigInteger.Parse(numeric) % 97):D2}{bban}";
    }

    private static async Task ConfigureWpsAsync(HttpClient client)
    {
        var settings = await client.GetAsync<JsonElement>("/api/hr/settings");
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText(), ErpFixture.Json)!;
        body["employerEid"] = "1234567";
        body["payerEid"] = "1234567";
        body["payerBankShortName"] = "QNBA";
        body["payerIban"] = Iban("QNBA", "000000000000693123456");
        (await client.PutAsJsonAsync("/api/hr/settings", body, ErpFixture.Json)).EnsureSuccessStatusCode();
    }

    private static Task<Employee> HireAsync(HttpClient client, string empNo, string name, string nationality, DateOnly joining, string qid,
        string bankAccount, string scheme = "None") =>
        client.PostAsync<Employee>("/api/hr/employees", new
        {
            empNo,
            nameEn = name,
            nationality,
            gender = "Male",
            joiningDate = joining,
            qid,
            iban = Iban("CBQA", bankAccount),
            socialInsuranceScheme = scheme
        });

    private static Task ContractAsync(HttpClient client, Guid employeeId, DateOnly start, params (string Code, decimal Amount)[] components) =>
        client.PostAsync<JsonElement>($"/api/hr/employees/{employeeId}/contracts", new
        {
            startDate = start,
            contractType = "Unlimited",
            probationMonths = 0,
            accommodationProvided = false,
            foodProvided = true,
            gratuityBasis = "Basic",
            components = components.Select(c => new { componentCode = c.Code, amount = c.Amount }).ToArray()
        });

    private sealed record Workforce(Employee Ramesh, Employee Maria, Employee Ahmed);

    private static async Task<Workforce> SetUpWorkforceAsync(HttpClient client)
    {
        await ConfigureWpsAsync(client);
        var ramesh = await HireAsync(client, "E001", "Ramesh Kumar", "IN", new DateOnly(2024, 3, 1), "28435612345", "000000000001111111111");
        var maria = await HireAsync(client, "E002", "Maria Santos", "PH", MonthStart.AddDays(15), "29160812345", "000000000002222222222");
        var ahmed = await HireAsync(client, "E003", "Ahmed Al-Kuwari", "QA", new DateOnly(2020, 1, 1), "27863412345", "000000000003333333333", "Grsia");
        await ContractAsync(client, ramesh.Id, new DateOnly(2024, 3, 1), ("BASIC", 3000), ("HOUSING", 1000), ("TRANSPORT", 500));
        await ContractAsync(client, maria.Id, MonthStart.AddDays(15), ("BASIC", 2500), ("HOUSING", 800));
        await ContractAsync(client, ahmed.Id, new DateOnly(2020, 1, 1), ("BASIC", 15000), ("HOUSING", 5000));
        return new Workforce(ramesh, maria, ahmed);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public async Task Full_payroll_cycle_posts_salaries_gratuity_wps_and_payment()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var staff = await SetUpWorkforceAsync(c);

        // Variable pay and unpaid leave for the month.
        await c.PostAsync<JsonElement>("/api/hr/payroll-inputs", new { employeeId = staff.Ramesh.Id, year = MonthStart.Year, month = MonthStart.Month, componentCode = "OVERTIME", overtimeType = "Normal", hours = 10m });
        await c.PostAsync<JsonElement>("/api/hr/payroll-inputs", new { employeeId = staff.Ramesh.Id, year = MonthStart.Year, month = MonthStart.Month, componentCode = "BONUS", amount = 250m });
        await c.PostAsync<JsonElement>("/api/hr/payroll-inputs", new { employeeId = staff.Maria.Id, year = MonthStart.Year, month = MonthStart.Month, componentCode = "OTHER_DED", amount = 100m });
        var leave = await c.PostAsync<JsonElement>("/api/hr/leave-requests", new { employeeId = staff.Ramesh.Id, leaveTypeCode = "UNPAID", fromDate = MonthStart.AddDays(2), toDate = MonthStart.AddDays(3) });
        await c.PostAsync<JsonElement>($"/api/hr/leave-requests/{leave.GetProperty("id").GetGuid()}/approve", new { });

        var run = await c.PostAsync<Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        run.DueDate.ShouldBe(MonthEnd.AddDays(1));
        run.WpsDeadline.ShouldBe(MonthEnd.AddDays(8));

        run = await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        run.Status.ShouldBe("Calculated");
        run.EmployeeCount.ShouldBe(3);

        var detail = await c.GetAsync<RunDetail>($"/api/hr/payroll-runs/{run.Id}");
        var ramesh = detail.Payslips.Single(p => p.EmpNo == "E001");
        ramesh.PaidDays.ShouldBe(28);
        ramesh.UnpaidDays.ShouldBe(2);
        // 3000×28/30 + 1000×28/30 + 500×28/30 + overtime 10h × (3000/240) × 1.25 + bonus
        ramesh.Gross.ShouldBe(2800m + 933.33m + 466.67m + 156.25m + 250m);
        ramesh.Net.ShouldBe(ramesh.Gross);

        var mariaDays = MonthEnd.Day - 15;
        var maria = detail.Payslips.Single(p => p.EmpNo == "E002");
        maria.PaidDays.ShouldBe(mariaDays);
        maria.Gross.ShouldBe(Round(2500m * mariaDays / 30) + Round(800m * mariaDays / 30));
        maria.Deductions.ShouldBe(100m);
        maria.Net.ShouldBe(maria.Gross - 100m);

        detail.Payslips.Single(p => p.EmpNo == "E003").Gross.ShouldBe(20000m);
        run.TotalNet.ShouldBe(detail.Payslips.Sum(p => p.Net));

        // Inputs are locked once payroll is approved.
        run = await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/approve", new { });
        var locked = await c.PostJsonAsync("/api/hr/payroll-inputs", new { employeeId = staff.Ramesh.Id, year = MonthStart.Year, month = MonthStart.Month, componentCode = "BONUS", amount = 1m });
        (await locked.ProblemAsync()).GetProperty("code").GetString().ShouldBe("hr.payroll_locked");

        run = await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/post", new { });
        run.Status.ShouldBe("Posted");
        run.JournalEntryId.ShouldNotBeNull();
        run.GratuityJournalEntryId.ShouldNotBeNull();

        // Gratuity: GRSIA-covered Qatari excluded; provision equals the computed liability per employee.
        var gratuity = await c.GetAsync<List<GratuityRow>>($"/api/hr/reports/gratuity-liability?asOf={MonthEnd:yyyy-MM-dd}");
        gratuity.Select(g => g.EmpNo).Order().ShouldBe(["E001", "E002"]);
        gratuity.ShouldAllBe(g => g.Liability == g.Provision);
        var rameshServiceDays = MonthEnd.DayNumber - new DateOnly(2024, 3, 1).DayNumber + 1 - 2; // unpaid leave excluded
        gratuity.Single(g => g.EmpNo == "E001").Provision.ShouldBe(Round(3000m * 21m / 30m * rameshServiceDays / 365m));
        run.TotalGratuityAccrual.ShouldBe(gratuity.Sum(g => g.Provision));

        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.Rows.Single(r => r.Code == "2140").Closing.ShouldBe(-run.TotalNet);
        tb.Rows.Single(r => r.Code == "2210").Closing.ShouldBe(-run.TotalGratuityAccrual);
        tb.Rows.Single(r => r.Code == "5210").Debit.ShouldBe(2800m + Round(2500m * mariaDays / 30) + 15000m);
        tb.Rows.Single(r => r.Code == "4210").Credit.ShouldBe(100m); // deduction recognised as other income

        // WPS Salary Information File.
        var wps = await c.PostAsync<WpsFile>($"/api/hr/payroll-runs/{run.Id}/wps-files", new { });
        wps.RecordCount.ShouldBe(3);
        wps.TotalSalaries.ShouldBe(run.TotalNet);
        wps.FileName.ShouldStartWith("SIF_1234567_QNBA_");
        var content = await c.GetStringAsync($"/api/hr/wps-files/{wps.Id}/download");
        content.ShouldContain($"{MonthStart:yyyyMM},{run.TotalNet:0.00},3,1");
        content.ShouldContain("RAMESH KUMAR");
        content.ShouldContain("28435612345");

        // Payment clears salaries payable against the bank.
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        var paid = await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/mark-paid",
            new { paymentAccountId = accounts.Single(a => a.Code == "1120").Id, paymentDate = MonthEnd });
        paid.Status.ShouldBe("Paid");
        tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        tb.Rows.Single(r => r.Code == "2140").Closing.ShouldBe(0m);
        tb.Rows.Single(r => r.Code == "1120").Closing.ShouldBe(-run.TotalNet);

        var reverse = await c.PostJsonAsync($"/api/hr/payroll-runs/{run.Id}/reverse", new { date = MonthEnd, reason = "test" });
        (await reverse.ProblemAsync()).GetProperty("code").GetString().ShouldBe("hr.payroll_paid");
    }

    [Fact]
    public async Task Reversing_a_posted_payroll_undoes_salaries_and_gratuity_and_allows_a_rerun()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await SetUpWorkforceAsync(c);

        var run = await c.PostAsync<Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/approve", new { });
        run = await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/post", new { });

        var duplicate = await c.PostJsonAsync("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var reversed = await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/reverse", new { date = MonthEnd, reason = "Wrong overtime" });
        reversed.Status.ShouldBe("Reversed");

        var tb = await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={MonthStart:yyyy-MM-dd}&to={MonthEnd:yyyy-MM-dd}");
        tb.IsBalanced.ShouldBeTrue();
        tb.Rows.Where(r => r.Code is "2140" or "2210" or "5210").ShouldAllBe(r => r.Closing == 0m);

        var gratuity = await c.GetAsync<List<GratuityRow>>($"/api/hr/reports/gratuity-liability?asOf={MonthEnd:yyyy-MM-dd}");
        gratuity.ShouldAllBe(g => g.Provision == 0m);

        // The month can be run again, and the gratuity accrues from zero again.
        var rerun = await c.PostAsync<Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        await c.PostAsync<Run>($"/api/hr/payroll-runs/{rerun.Id}/calculate", new { });
        await c.PostAsync<Run>($"/api/hr/payroll-runs/{rerun.Id}/approve", new { });
        rerun = await c.PostAsync<Run>($"/api/hr/payroll-runs/{rerun.Id}/post", new { });
        rerun.TotalGratuityAccrual.ShouldBe(run.TotalGratuityAccrual);
    }

    [Fact]
    public async Task Calculation_lists_every_employee_problem_and_saves_nothing()
    {
        var t = await fixture.CreateTenantAsync();
        await HireAsync(t.Client, "E100", "No Contract", "NP", new DateOnly(2025, 1, 1), "28435699999", "000000000009999999999");
        var run = await t.Client.PostAsync<Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });

        var response = await t.Client.PostJsonAsync($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.ProblemAsync();
        problem.GetProperty("code").GetString().ShouldBe("hr.payroll_errors");
        problem.GetProperty("details").GetProperty("errors")[0].GetString()!.ShouldContain("E100");

        (await t.Client.GetAsync<RunDetail>($"/api/hr/payroll-runs/{run.Id}")).Run.Status.ShouldBe("Draft");
    }

    [Fact]
    public async Task Contracts_enforce_qatar_minimum_wage_and_allowances()
    {
        var t = await fixture.CreateTenantAsync();
        var e = await HireAsync(t.Client, "E200", "Low Wage", "BD", new DateOnly(2025, 1, 1), "28435688888", "000000000008888888888");

        var belowMinimum = await t.Client.PostJsonAsync($"/api/hr/employees/{e.Id}/contracts", new
        {
            startDate = new DateOnly(2025, 1, 1), contractType = "Limited", probationMonths = 3, accommodationProvided = true, foodProvided = true,
            gratuityBasis = "Basic", components = new[] { new { componentCode = "BASIC", amount = 900m } }
        });
        (await belowMinimum.ProblemAsync()).GetProperty("code").GetString().ShouldBe("hr.minimum_wage");

        var noHousing = await t.Client.PostJsonAsync($"/api/hr/employees/{e.Id}/contracts", new
        {
            startDate = new DateOnly(2025, 1, 1), contractType = "Limited", probationMonths = 3, accommodationProvided = false, foodProvided = true,
            gratuityBasis = "Basic", components = new[] { new { componentCode = "BASIC", amount = 1000m } }
        });
        (await noHousing.ProblemAsync()).GetProperty("code").GetString().ShouldBe("hr.minimum_housing");
    }

    [Fact]
    public async Task Identifiers_are_validated_encrypted_and_masked_without_permission()
    {
        var t = await fixture.CreateTenantAsync();
        var badIban = await t.Client.PostJsonAsync("/api/hr/employees", new
        {
            empNo = "E300", nameEn = "Bad Iban", nationality = "IN", gender = "Male", joiningDate = new DateOnly(2025, 1, 1), iban = "QA00QNBA000000000000000000000"
        });
        (await badIban.ProblemAsync()).GetProperty("code").GetString().ShouldBe("hr.invalid_iban");

        var e = await HireAsync(t.Client, "E301", "Sunil Thapa", "NP", new DateOnly(2025, 1, 1), "28435677777", "000000000007777777777");
        e.Qid.ShouldBe("28435677777");
        e.SensitiveMasked.ShouldBeFalse();

        var duplicateQid = await t.Client.PostJsonAsync("/api/hr/employees", new
        {
            empNo = "E302", nameEn = "Same Qid", nationality = "NP", gender = "Male", joiningDate = new DateOnly(2025, 1, 1), qid = "28435677777"
        });
        duplicateQid.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Stored encrypted at rest.
        await using (var connection = await fixture.OpenAppConnectionAsync(t.TenantId))
        {
            var stored = (string)(await new Npgsql.NpgsqlCommand($"SELECT qid_encrypted FROM hr.employee WHERE id = '{e.Id}'", connection).ExecuteScalarAsync())!;
            stored.ShouldStartWith("v1:");
            stored.ShouldNotContain("28435677777");
        }

        // An accountant can see employees but not their identifiers.
        await (await t.Client.PostJsonAsync("/api/core/users", new
        {
            email = "acc@test.qa", displayNameEn = "Accountant", password = "Accountant-Pass-1", preferredLanguage = "en", roles = new[] { "accountant" }
        })).EnsureSuccessAsync();
        var accountant = await fixture.LoginAsync(t.Code, "acc@test.qa", "Accountant-Pass-1");
        var masked = await accountant.GetAsync<Employee>($"/api/hr/employees/{e.Id}");
        masked.SensitiveMasked.ShouldBeTrue();
        masked.Qid.ShouldBe("•••••••7777");
        (await accountant.GetAsync($"/api/hr/employees/{e.Id}/contracts")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Search by exact QID works through the blind index.
        var found = await t.Client.GetAsync<JsonElement>("/api/hr/employees?search=28435677777");
        found.GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Sick_leave_is_paid_in_full_then_half_and_unpaid_before_minimum_service()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var senior = await HireAsync(c, "K001", "Senior Staff", "IN", new DateOnly(2024, 1, 1), "28435655555", "000000000005050505050");
        var newcomer = await HireAsync(c, "K002", "New Joiner", "IN", MonthStart.AddMonths(-1), "28435655556", "000000000005050505051");
        await ContractAsync(c, senior.Id, new DateOnly(2024, 1, 1), ("BASIC", 3000), ("HOUSING", 1000));
        await ContractAsync(c, newcomer.Id, MonthStart.AddMonths(-1), ("BASIC", 3000), ("HOUSING", 1000));

        foreach (var (employee, days) in new[] { (senior, 20), (newcomer, 4) })
        {
            var sick = await c.PostAsync<JsonElement>("/api/hr/leave-requests",
                new { employeeId = employee.Id, leaveTypeCode = "SICK", fromDate = MonthStart, toDate = MonthStart.AddDays(days - 1) });
            await c.PostAsync<JsonElement>($"/api/hr/leave-requests/{sick.GetProperty("id").GetGuid()}/approve", new { });
        }

        var run = await c.PostAsync<Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        await c.PostAsync<Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        var detail = await c.GetAsync<RunDetail>($"/api/hr/payroll-runs/{run.Id}");

        // 20 sick days, first of the service year: 14 at full pay, 6 at half pay → 3 days' pay lost.
        var s = detail.Payslips.Single(p => p.EmpNo == "K001");
        s.HalfPayDays.ShouldBe(6);
        s.UnpaidDays.ShouldBe(0);
        s.PaidDays.ShouldBe(27);
        s.Gross.ShouldBe(2700m + 900m);

        // Less than 3 months' service: sick leave is unpaid.
        var n = detail.Payslips.Single(p => p.EmpNo == "K002");
        n.UnpaidDays.ShouldBe(4);
        n.PaidDays.ShouldBe(26);
    }

    [Fact]
    public async Task Expiring_documents_are_listed_with_days_left()
    {
        var t = await fixture.CreateTenantAsync();
        var e = await HireAsync(t.Client, "E400", "Expiry Test", "EG", new DateOnly(2025, 1, 1), "28435666666", "000000000006666666666");
        await t.Client.PostAsync<JsonElement>($"/api/hr/employees/{e.Id}/documents", new { docType = "Qid", docNo = "28435666666", expiryDate = Today.AddDays(30) });
        await t.Client.PostAsync<JsonElement>($"/api/hr/employees/{e.Id}/documents", new { docType = "Passport", docNo = "A1234567", expiryDate = Today.AddDays(400) });
        await t.Client.PostAsync<JsonElement>("/api/hr/company-documents", new { docType = "CommercialRegistration", docNo = "123456", expiryDate = Today.AddDays(-3) });

        var expiring = await t.Client.GetAsync<List<JsonElement>>("/api/hr/documents/expiring?withinDays=90");
        expiring.Count.ShouldBe(2);
        expiring[0].GetProperty("status").GetString().ShouldBe("Expired");
        expiring[0].GetProperty("ownerNameAr").GetString().ShouldBe("السجل التجاري");
        expiring[1].GetProperty("daysLeft").GetInt32().ShouldBe(30);
    }
}

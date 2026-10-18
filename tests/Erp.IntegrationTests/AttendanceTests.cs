using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class AttendanceTests(ErpFixture fixture)
{
    private static readonly DateOnly MonthStart = new(FinanceTests.Today.Year, FinanceTests.Today.Month, 1);

    private static DateOnly First(DayOfWeek day) => Enumerable.Range(0, 7).Select(MonthStart.AddDays).First(d => d.DayOfWeek == day);

    private static async Task<JsonElement> UploadAsync(HttpClient client, string csv, bool dryRun)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(part, "file", "attendance.csv");
        var response = await client.PostAsync($"/api/hr/imports/attendance?dryRun={dryRun.ToString().ToLowerInvariant()}", content);
        return await response.ReadAsync<JsonElement>();
    }

    [Fact]
    public async Task Attendance_import_builds_overtime_inputs_that_payroll_pays()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var employeeId = await SettlementTests.HireWithContractAsync(c, "A001", new DateOnly(2025, 1, 1), "28435677777", "000000000007070707070");
        var workday = First(DayOfWeek.Sunday);
        var friday = First(DayOfWeek.Friday);

        // Unknown employee and a missing punch: the whole file is rejected.
        var bad = await UploadAsync(c, $"EmpNo,Date,In,Out\nA001,{workday:dd/MM/yyyy},07:00,17:30\nB999,{workday:dd/MM/yyyy},07:00,15:00\nA001,{friday:dd/MM/yyyy},08:00,\n", dryRun: false);
        bad.GetProperty("committed").GetBoolean().ShouldBeFalse();
        bad.GetProperty("validRows").GetInt32().ShouldBe(1);
        bad.GetProperty("totalRows").GetInt32().ShouldBe(3);

        // A manual overtime input is kept alongside the generated ones.
        await c.PostAsync<JsonElement>("/api/hr/payroll-inputs", new
        {
            employeeId, year = MonthStart.Year, month = MonthStart.Month, componentCode = "OVERTIME", overtimeType = "Holiday", hours = 3m
        });

        var csv = $"EmpNo,Date,In,Out\nA001,{workday:dd/MM/yyyy},07:00,17:30\nA001,{friday:dd/MM/yyyy},08:00,12:00\n";
        var preview = await UploadAsync(c, csv, dryRun: true);
        preview.GetProperty("committed").GetBoolean().ShouldBeFalse();
        var summary = preview.GetProperty("employees")[0];
        summary.GetProperty("normal").GetDecimal().ShouldBe(2.5m);
        summary.GetProperty("restDay").GetDecimal().ShouldBe(4m);

        (await UploadAsync(c, csv, dryRun: false)).GetProperty("committed").GetBoolean().ShouldBeTrue();
        // Re-importing the working day with new punches replaces it rather than adding to it.
        var again = await UploadAsync(c, $"EmpNo;Date;In;Out\nA001;{workday:yyyy-MM-dd};07:00;18:00\n", dryRun: false);
        again.GetProperty("committed").GetBoolean().ShouldBeTrue();

        var inputs = await c.GetAsync<List<JsonElement>>($"/api/hr/payroll-inputs?year={MonthStart.Year}&month={MonthStart.Month}");
        decimal Hours(string source, string type) => inputs.Where(i => i.GetProperty("source").GetString() == source && i.GetProperty("overtimeType").GetString() == type)
            .Sum(i => i.GetProperty("hours").GetDecimal());
        Hours("Attendance", "Normal").ShouldBe(3m);
        Hours("Attendance", "RestDay").ShouldBe(4m);
        Hours("Manual", "Holiday").ShouldBe(3m);

        var month = await c.GetAsync<List<JsonElement>>($"/api/hr/attendance?year={MonthStart.Year}&month={MonthStart.Month}");
        month.Single().GetProperty("days").GetInt32().ShouldBe(2);

        // Basic 3,000 → 12.50/hour: 3 h × 1.25 + 4 h × 1.5 + 3 h × 1.5 = 46.88 + 75 + 56.25.
        var run = await c.PostAsync<PayrollTests.Run>("/api/hr/payroll-runs", new { year = MonthStart.Year, month = MonthStart.Month });
        run = await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/calculate", new { });
        run.TotalGross.ShouldBe(4000m + 46.88m + 75m + 56.25m);

        // Once approved, the month's attendance is locked.
        await c.PostAsync<PayrollTests.Run>($"/api/hr/payroll-runs/{run.Id}/approve", new { });
        (await UploadAsync(c, csv, dryRun: true)).GetProperty("validRows").GetInt32().ShouldBe(0);
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class RosterTests(ErpFixture fixture)
{
    public sealed record Shift(Guid Id, string Code, decimal Hours);
    public sealed record Cell(Guid EmployeeId, DateOnly WorkDate, Guid? ShiftId, bool Off);
    public sealed record View(List<Cell> Cells);
    public sealed record Row(Guid EmployeeId, int RosteredDays, int WorkedDays, int AbsentDays, int LateCount, int LateMinutes, int EarlyCount, int EarlyMinutes,
        int UnrosteredDays, decimal RosteredHours);
    public sealed record Report(List<Row> Rows, List<JsonElement> Exceptions);

    private static async Task<JsonElement> UploadAsync(HttpClient client, string csv, bool dryRun)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(part, "file", "attendance.csv");
        return await (await client.PostAsync($"/api/hr/imports/attendance?dryRun={dryRun.ToString().ToLowerInvariant()}", content)).ReadAsync<JsonElement>();
    }

    [Fact]
    public async Task Rosters_set_rest_days_for_overtime_and_show_lateness_early_leaving_and_absence()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var employee = await SettlementTests.HireWithContractAsync(c, "R001", new DateOnly(2025, 1, 1), "28435688888", "000000000008080808080");
        // A whole week in the past (Sunday to Saturday), so every missed day counts as an absence.
        var today = FinanceTests.Today;
        var sunday = today.AddDays(-14 - (int)today.DayOfWeek);
        var day = Enumerable.Range(0, 7).Select(sunday.AddDays).ToDictionary(d => d.DayOfWeek);

        var morning = await c.PostAsync<Shift>("/api/hr/shifts", new
        {
            code = "MORN", nameEn = "Morning", nameAr = "صباحي", startTime = "07:00", endTime = "15:00", graceMinutes = 10
        });
        morning.Hours.ShouldBe(8m);
        var night = await c.PostAsync<Shift>("/api/hr/shifts", new
        {
            code = "NIGHT", nameEn = "Night", nameAr = "ليلي", startTime = "22:00", endTime = "06:30", breakMinutes = 30
        });
        night.Hours.ShouldBe(8m);

        // Mornings all week with Thursday off (so Friday is a working day for this employee), then Saturday moved to nights.
        await c.PostAsync<JsonElement>("/api/hr/rosters/assign", new
        {
            employeeIds = new[] { employee }, from = sunday, to = sunday.AddDays(6), shiftId = morning.Id, restDays = new[] { "Thursday" }
        });
        (await c.PutAsJsonAsync("/api/hr/rosters", new[] { new { employeeId = employee, workDate = day[DayOfWeek.Saturday], shiftId = night.Id } }))
            .EnsureSuccessStatusCode();
        var view = await c.GetAsync<View>($"/api/hr/rosters?from={sunday:yyyy-MM-dd}&to={sunday.AddDays(6):yyyy-MM-dd}");
        view.Cells.Count.ShouldBe(7);
        view.Cells.Single(x => x.WorkDate == day[DayOfWeek.Thursday]).Off.ShouldBeTrue();
        view.Cells.Single(x => x.WorkDate == day[DayOfWeek.Saturday]).ShiftId.ShouldBe(night.Id);

        // Sun on time (5 min is within grace), Mon 30 min late, Tue left an hour early, Wed absent, Thu worked on the day off,
        // Fri a normal working day, Sat the night shift into Sunday morning.
        var csv = new StringBuilder("EmpNo,Date,In,Out\n")
            .Append($"R001,{day[DayOfWeek.Sunday]:dd/MM/yyyy},07:05,15:00\n")
            .Append($"R001,{day[DayOfWeek.Monday]:dd/MM/yyyy},07:30,15:00\n")
            .Append($"R001,{day[DayOfWeek.Tuesday]:dd/MM/yyyy},07:00,14:00\n")
            .Append($"R001,{day[DayOfWeek.Thursday]:dd/MM/yyyy},08:00,12:00\n")
            .Append($"R001,{day[DayOfWeek.Friday]:dd/MM/yyyy},07:00,15:00\n")
            .Append($"R001,{day[DayOfWeek.Saturday]:dd/MM/yyyy},22:00,06:30\n").ToString();
        var import = await UploadAsync(c, csv, dryRun: false);
        import.GetProperty("committed").GetBoolean().ShouldBeTrue();
        // Thursday is the rostered rest day: its 4 hours are rest-day overtime; Friday's 8 hours are an ordinary day.
        var summary = import.GetProperty("employees")[0];
        summary.GetProperty("restDay").GetDecimal().ShouldBe(4m);

        var report = await c.GetAsync<Report>($"/api/hr/rosters/report?from={sunday:yyyy-MM-dd}&to={sunday.AddDays(6):yyyy-MM-dd}");
        var row = report.Rows.Single(r => r.EmployeeId == employee);
        row.RosteredDays.ShouldBe(6);
        row.RosteredHours.ShouldBe(48m);
        row.WorkedDays.ShouldBe(6);
        row.AbsentDays.ShouldBe(1);
        row.LateCount.ShouldBe(1);
        row.LateMinutes.ShouldBe(30);
        row.EarlyCount.ShouldBe(1);
        row.EarlyMinutes.ShouldBe(60);
        report.Exceptions.Single(x => x.GetProperty("kind").GetString() == "Absent").GetProperty("workDate").GetString()
            .ShouldBe(day[DayOfWeek.Wednesday].ToString("yyyy-MM-dd"));

        // Copy the week forward; clearing a day removes it.
        var next = await c.PostAsync<JsonElement>("/api/hr/rosters/copy-week", new { sourceWeekStart = sunday, targetWeekStart = sunday.AddDays(7) });
        next.GetProperty("saved").GetInt32().ShouldBe(7);
        (await c.PutAsJsonAsync("/api/hr/rosters", new[] { new { employeeId = employee, workDate = sunday.AddDays(7), shiftId = (Guid?)null, off = false } }))
            .EnsureSuccessStatusCode();
        (await c.GetAsync<View>($"/api/hr/rosters?from={sunday.AddDays(7):yyyy-MM-dd}&to={sunday.AddDays(13):yyyy-MM-dd}")).Cells.Count.ShouldBe(6);
    }
}

using Erp.Modules.Core.Platform;
using Erp.SharedKernel.Application;
using System.Text;

namespace Erp.UnitTests;

public sealed class ReportScheduleRuleTests
{
    private static readonly DateOnly Wednesday = new(2026, 10, 14);

    [Theory]
    [InlineData(ReportFrequency.Daily, null, null, "2026-10-15")]
    [InlineData(ReportFrequency.Weekly, 0, null, "2026-10-18")]   // next Sunday
    [InlineData(ReportFrequency.Weekly, 3, null, "2026-10-21")]   // Wednesday → the following Wednesday
    [InlineData(ReportFrequency.Monthly, null, 20, "2026-10-20")]
    [InlineData(ReportFrequency.Monthly, null, 1, "2026-11-01")]
    [InlineData(ReportFrequency.Monthly, null, 14, "2026-11-14")]
    public void Next_run_is_strictly_after_the_given_day(ReportFrequency f, int? weekday, int? monthDay, string expected) =>
        ReportScheduleRules.NextRun(f, weekday, monthDay, Wednesday).ShouldBe(DateOnly.Parse(expected));

    [Fact]
    public void Csv_has_a_bom_quotes_commas_and_neutralises_formulas()
    {
        var bytes = CsvFile.Build(["A", "B"], [["x,y", 1234.5m], ["=SUM(A1)", "-5"]]);
        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        var text = Encoding.UTF8.GetString(bytes[3..]);
        text.ShouldContain("\"x,y\",1234.50");
        text.ShouldContain("'=SUM(A1),-5");
    }
}

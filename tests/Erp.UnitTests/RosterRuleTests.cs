using Erp.Modules.HR.Payroll;
using Erp.Modules.HR.Rosters;

namespace Erp.UnitTests;

public sealed class RosterRuleTests
{
    private static readonly DateOnly Day = new(2026, 3, 1);
    private static TimeOnly T(string v) => TimeOnly.Parse(v);

    [Theory]
    [InlineData("07:00", "15:00", 0, 8)]
    [InlineData("22:00", "06:30", 30, 8)]   // past midnight, less the break
    [InlineData("09:00", "18:00", 60, 8)]
    public void Shift_hours_cross_midnight_and_exclude_the_break(string start, string end, int breakMinutes, decimal hours) =>
        RosterRules.ShiftHours(T(start), T(end), breakMinutes).ShouldBe(hours);

    [Theory]
    [InlineData("07:00", "15:00", 10, "07:08", "15:00", 0, 0)]    // within grace
    [InlineData("07:00", "15:00", 10, "07:25", "15:00", 25, 0)]   // beyond grace: all minutes count
    [InlineData("07:00", "15:00", 10, "06:50", "14:15", 0, 45)]
    [InlineData("22:00", "06:00", 0, "22:15", "05:30", 15, 30)]   // night shift ending next morning
    [InlineData("06:00", "14:00", 0, "23:50", "14:00", 0, 0)]     // early-morning shift, punched in the evening before
    public void Lateness_and_early_leaving_are_measured_against_the_shift(string start, string end, int grace, string timeIn, string timeOut, int late, int early) =>
        RosterRules.Compare(Day, T(start), T(end), grace, T(timeIn), T(timeOut)).ShouldBe((late, early));

    [Fact]
    public void A_rostered_day_off_is_the_rest_day_instead_of_friday()
    {
        var friday = new DateOnly(2026, 3, 6);
        AttendanceCalculator.Calculate(friday, T("07:00"), T("15:00"), 8, false, isRestDay: false).RestDay.ShouldBe(0);
        AttendanceCalculator.Calculate(Day, T("07:00"), T("11:00"), 8, false, isRestDay: true).RestDay.ShouldBe(4);
        AttendanceCalculator.Calculate(friday, T("07:00"), T("11:00"), 8, false).RestDay.ShouldBe(4);
    }
}

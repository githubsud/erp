using Erp.Modules.HR.Payroll;

namespace Erp.UnitTests;

public sealed class AttendanceTests
{
    private static readonly DateOnly Sunday = new(2026, 3, 1);
    private static readonly DateOnly Friday = new(2026, 3, 6);

    [Theory]
    [InlineData("07:00", "17:30", 10.5, 2.5, 0)]   // day shift, 2.5 h overtime
    [InlineData("08:00", "15:00", 7, 0, 0)]        // short day, no overtime
    [InlineData("13:00", "23:00", 10, 0, 2)]       // overtime falls after 21:00
    [InlineData("12:00", "22:00", 10, 1, 1)]       // overtime straddles 21:00
    [InlineData("20:00", "06:00", 10, 0, 2)]       // night shift past midnight, overtime 04:00–06:00
    public void Working_day_overtime_is_split_into_normal_and_night(string timeIn, string timeOut, decimal worked, decimal normal, decimal night)
    {
        var h = AttendanceCalculator.Calculate(Sunday, TimeOnly.Parse(timeIn), TimeOnly.Parse(timeOut), 8, isHoliday: false);
        h.Worked.ShouldBe(worked);
        h.Normal.ShouldBe(normal);
        h.Night.ShouldBe(night);
        h.RestDay.ShouldBe(0);
    }

    [Fact]
    public void Every_hour_on_the_rest_day_or_a_holiday_is_overtime()
    {
        var friday = AttendanceCalculator.Calculate(Friday, new TimeOnly(8, 0), new TimeOnly(12, 15), 8, isHoliday: false);
        friday.RestDay.ShouldBe(4.25m);
        friday.Normal.ShouldBe(0);

        var holiday = AttendanceCalculator.Calculate(Sunday, new TimeOnly(8, 0), new TimeOnly(18, 0), 8, isHoliday: true);
        holiday.Holiday.ShouldBe(10);
        holiday.Overtime.ShouldBe(10);
    }
}

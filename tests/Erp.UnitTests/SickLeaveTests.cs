using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;

namespace Erp.UnitTests;

public sealed class SickLeaveTests
{
    [Theory]
    [InlineData(0, 20, 14, 6, 0)]    // first sick spell of the year: 2 weeks full, rest half
    [InlineData(10, 10, 4, 6, 0)]    // 10 days already taken this service year
    [InlineData(40, 5, 0, 2, 3)]     // half-pay band nearly used up
    [InlineData(50, 4, 0, 0, 4)]     // beyond 6 weeks: unpaid
    [InlineData(0, 0, 0, 0, 0)]
    public void Bands_follow_labour_law_defaults(int prior, int now, int full, int half, int unpaid)
    {
        var a = SickLeaveCalculator.Allocate(prior, now, 14, 28, eligible: true);
        a.ShouldBe(new SickLeaveAllocation(full, half, unpaid));
    }

    [Fact]
    public void Before_minimum_service_sick_leave_is_unpaid() =>
        SickLeaveCalculator.Allocate(0, 5, 14, 28, eligible: false).ShouldBe(new SickLeaveAllocation(0, 0, 5));

    [Theory]
    [InlineData("2024-03-01", "2026-10-01", "2026-03-01")]
    [InlineData("2024-03-01", "2026-02-15", "2025-03-01")]
    [InlineData("2024-03-01", "2024-03-01", "2024-03-01")]
    [InlineData("2024-03-01", "2024-01-01", "2024-03-01")]
    public void Service_year_starts_on_the_last_joining_anniversary(string joining, string date, string expected) =>
        SickLeaveCalculator.ServiceYearStart(DateOnly.Parse(joining), DateOnly.Parse(date)).ShouldBe(DateOnly.Parse(expected));

    [Fact]
    public void Half_pay_days_cost_half_a_day_each()
    {
        var basic = new ComponentRef(Guid.NewGuid(), "BASIC", "Basic", "أساسي", ComponentKind.Earning, WpsField.Basic, true);
        var input = new EmployeePayInput(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1), null,
            [new FixedPay(basic, 3000)], [], [], UnpaidLeaveDays: 0, HalfPayLeaveDays: 6);
        var r = PayrollCalculator.Calculate(input, new PayrollPolicy(30, 8, 1.25m, 1.5m, 1.5m, 1.5m, 50, 1000));
        r.PaidDays.ShouldBe(27);
        r.HalfPayDays.ShouldBe(6);
        r.Gross.ShouldBe(2700m);
    }
}

using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;

namespace Erp.UnitTests;

/// <summary>Golden cases. Expected values are hand-calculated on the 30-day basis (daily rate = monthly / 30).</summary>
public sealed class PayrollCalculatorTests
{
    private static readonly PayrollPolicy Policy = new(30, 8, 1.25m, 1.5m, 1.5m, 1.5m, 50, 1000);

    private static readonly ComponentRef Basic = new(Guid.NewGuid(), "BASIC", "Basic", "أساسي", ComponentKind.Earning, WpsField.Basic, true);
    private static readonly ComponentRef Housing = new(Guid.NewGuid(), "HOUSING", "Housing", "سكن", ComponentKind.Earning, WpsField.Housing, true);
    private static readonly ComponentRef Transport = new(Guid.NewGuid(), "TRANSPORT", "Transport", "مواصلات", ComponentKind.Earning, WpsField.Transport, true);
    private static readonly ComponentRef Overtime = new(Guid.NewGuid(), "OVERTIME", "Overtime", "إضافي", ComponentKind.Earning, WpsField.Overtime, false);
    private static readonly ComponentRef Bonus = new(Guid.NewGuid(), "BONUS", "Bonus", "مكافأة", ComponentKind.Earning, WpsField.ExtraIncome, false);
    private static readonly ComponentRef Deduction = new(Guid.NewGuid(), "OTHER_DED", "Deduction", "خصم", ComponentKind.Deduction, WpsField.Deduction, false);

    private static EmployeePayInput Input(DateOnly start, DateOnly end, DateOnly joining, DateOnly? termination = null, decimal unpaid = 0,
        IReadOnlyList<OvertimeHours>? overtime = null, IReadOnlyList<VariablePay>? variable = null, decimal basic = 3000) =>
        new(start, end, joining, termination,
            [new FixedPay(Basic, basic), new FixedPay(Housing, 1000), new FixedPay(Transport, 500)],
            variable ?? [], overtime ?? [], unpaid);

    [Fact]
    public void Full_month_pays_full_fixed_amounts()
    {
        var r = PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1)), Policy);
        r.PaidDays.ShouldBe(30);
        r.Gross.ShouldBe(4500m);
        r.Net.ShouldBe(4500m);
        r.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Joining_mid_month_prorates_by_calendar_days_on_a_30_day_basis()
    {
        // June has 30 days; joined on the 16th → 15 days.
        var r = PayrollCalculator.Calculate(Input(new(2026, 6, 1), new(2026, 6, 30), new(2026, 6, 16)), Policy);
        r.PaidDays.ShouldBe(15);
        r.Lines.Single(l => l.Component.Code == "BASIC").Amount.ShouldBe(1500m);
        r.Lines.Single(l => l.Component.Code == "HOUSING").Amount.ShouldBe(500m);
        r.Gross.ShouldBe(2250m);
    }

    [Fact]
    public void Termination_in_february_counts_days_employed()
    {
        var r = PayrollCalculator.Calculate(Input(new(2026, 2, 1), new(2026, 2, 28), new(2023, 5, 1), termination: new(2026, 2, 14)), Policy);
        r.PaidDays.ShouldBe(14);
        r.Lines.Single(l => l.Component.Code == "BASIC").Amount.ShouldBe(1400m); // 3000 × 14/30
    }

    [Fact]
    public void Unpaid_leave_reduces_paid_days()
    {
        var r = PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1), unpaid: 3), Policy);
        r.PaidDays.ShouldBe(27);
        r.UnpaidDays.ShouldBe(3);
        r.Lines.Single(l => l.Component.Code == "BASIC").Amount.ShouldBe(2700m);
        r.Gross.ShouldBe(4050m);
    }

    [Fact]
    public void Overtime_uses_basic_hourly_rate_times_the_multiplier()
    {
        // Hourly basic = 3000 / (30 × 8) = 12.50. Normal: 10 h × 12.50 × 1.25 = 156.25. Night: 4 h × 12.50 × 1.5 = 75.00.
        var r = PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1),
            overtime: [new OvertimeHours(Overtime, OvertimeType.Normal, 6), new OvertimeHours(Overtime, OvertimeType.Normal, 4),
                new OvertimeHours(Overtime, OvertimeType.Night, 4)]), Policy);
        var lines = r.Lines.Where(l => l.Component.Code == "OVERTIME").ToList();
        lines.Count.ShouldBe(2);
        lines.Single(l => l.Quantity == 10).Amount.ShouldBe(156.25m);
        lines.Single(l => l.Quantity == 4).Amount.ShouldBe(75m);
        r.OvertimeHours.ShouldBe(14);
        r.Gross.ShouldBe(4500m + 156.25m + 75m);
    }

    [Fact]
    public void Deductions_above_the_cap_are_an_error()
    {
        var r = PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1),
            variable: [new VariablePay(Deduction, 2300)]), Policy);
        r.Errors.ShouldContain(e => e.Contains("exceed 50%"));
    }

    [Fact]
    public void Deductions_within_the_cap_reduce_net_pay()
    {
        var r = PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1),
            variable: [new VariablePay(Deduction, 200), new VariablePay(Bonus, 300)]), Policy);
        r.Gross.ShouldBe(4800m);
        r.Deductions.ShouldBe(200m);
        r.Net.ShouldBe(4600m);
        r.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Basic_below_minimum_wage_is_flagged()
    {
        var r = PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1), basic: 900), Policy);
        r.Warnings.ShouldContain(w => w.Contains("legal minimum"));
    }

    [Fact]
    public void Employee_not_employed_in_the_period_is_not_payable()
    {
        PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2026, 11, 3)), Policy).IsPayable.ShouldBeFalse();
        PayrollCalculator.Calculate(Input(new(2026, 10, 1), new(2026, 10, 31), new(2020, 1, 1), termination: new(2026, 9, 30)), Policy)
            .IsPayable.ShouldBeFalse();
    }

    [Fact]
    public void Missing_basic_salary_is_an_error()
    {
        var input = new EmployeePayInput(new(2026, 10, 1), new(2026, 10, 31), new(2024, 1, 1), null, [new FixedPay(Housing, 1000)], [], [], 0);
        PayrollCalculator.Calculate(input, Policy).Errors.ShouldContain(e => e.Contains("basic"));
    }
}

public sealed class GratuityCalculatorTests
{
    [Fact]
    public void One_year_of_service_earns_three_weeks_basic()
    {
        var days = GratuityCalculator.ServiceDays(new(2025, 1, 1), new(2025, 12, 31), 0);
        days.ShouldBe(365);
        GratuityCalculator.Liability(5000, 21, 30, days).ShouldBe(3500m); // 5000 / 30 × 21
    }

    [Fact]
    public void Liability_is_pro_rata_for_part_years()
    {
        // 182 days: 3500 × 182 / 365 = 1745.205... → 1745.21
        GratuityCalculator.Liability(5000, 21, 30, 182).ShouldBe(1745.21m);
    }

    [Fact]
    public void Contract_can_grant_more_than_the_legal_minimum()
    {
        GratuityCalculator.Liability(6000, 30, 30, 365).ShouldBe(6000m); // one month per year
    }

    [Fact]
    public void Unpaid_leave_is_excluded_from_service()
    {
        GratuityCalculator.ServiceDays(new(2025, 1, 1), new(2025, 12, 31), 15).ShouldBe(350);
    }

    [Fact]
    public void Payout_requires_one_full_year()
    {
        GratuityCalculator.IsEligibleForPayout(new(2025, 10, 18), new(2026, 10, 17), 12).ShouldBeTrue();
        GratuityCalculator.IsEligibleForPayout(new(2025, 10, 18), new(2026, 10, 16), 12).ShouldBeFalse();
    }

    [Fact]
    public void Annual_leave_is_three_weeks_then_four_after_five_years()
    {
        GratuityCalculator.AnnualLeaveDaysPerYear(new(2022, 1, 1), new(2026, 12, 31), null).ShouldBe(21);
        GratuityCalculator.AnnualLeaveDaysPerYear(new(2021, 1, 1), new(2026, 1, 1), null).ShouldBe(28);
        GratuityCalculator.AnnualLeaveDaysPerYear(new(2025, 1, 1), new(2026, 1, 1), 30).ShouldBe(30);
    }
}

using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;
using Erp.SharedKernel.Domain;

namespace Erp.UnitTests;

public sealed class AmountInWordsTests
{
    [Theory]
    [InlineData(3500.50, "Three Thousand Five Hundred Qatari Riyals and Fifty Dirhams Only")]
    [InlineData(1, "One Qatari Riyal Only")]
    [InlineData(0.01, "Zero Qatari Riyals and One Dirham Only")]
    [InlineData(1234567.89, "One Million Two Hundred Thirty-Four Thousand Five Hundred Sixty-Seven Qatari Riyals and Eighty-Nine Dirhams Only")]
    [InlineData(4656.25, "Four Thousand Six Hundred Fifty-Six Qatari Riyals and Twenty-Five Dirhams Only")]
    public void English(decimal amount, string expected) => AmountInWords.English(amount).ShouldBe(expected);

    [Theory]
    [InlineData(3500.50, "فقط ثلاثة آلاف وخمسمائة ريال قطري وخمسون درهماً لا غير")]
    [InlineData(2000, "فقط ألفان ريال قطري لا غير")]
    [InlineData(5.05, "فقط خمسة ريال قطري وخمسة دراهم لا غير")]
    [InlineData(1100, "فقط ألف ومائة ريال قطري لا غير")]
    public void Arabic(decimal amount, string expected) => AmountInWords.Arabic(amount).ShouldBe(expected);

    [Theory]
    [InlineData(345, "ثلاثمائة وخمسة وأربعون")]
    [InlineData(15000, "خمسة عشر ألف")]
    [InlineData(2_500_000, "مليونان وخمسمائة ألف")]
    [InlineData(21, "واحد وعشرون")]
    public void Arabic_numbers(long n, string expected) => AmountInWords.ArabicNumber(n).ShouldBe(expected);
}

public sealed class SettlementCalculatorTests
{
    private static readonly ComponentRef Basic = new(Guid.NewGuid(), "BASIC", "Basic Salary", "الراتب الأساسي", ComponentKind.Earning, WpsField.Basic, true);
    private static readonly ComponentRef Housing = new(Guid.NewGuid(), "HOUSING", "Housing", "سكن", ComponentKind.Earning, WpsField.Housing, true);

    private static SettlementInput Input(DateOnly joining, DateOnly lastDay, decimal leaveTaken = 30, DateOnly? unpaidFrom = null,
        decimal advance = 0, decimal notice = 0, bool gratuityApplies = true) =>
        new(joining, lastDay, 0, gratuityApplies, 3000, 21, 30, 12, 21, leaveTaken, 3000, 30, unpaidFrom, 0,
            [new FixedPay(Basic, 3000), new FixedPay(Housing, 1000)], notice, 0, 0, advance, 0);

    [Fact]
    public void Long_service_settlement_includes_gratuity_leave_salary_and_recoveries()
    {
        var r = SettlementCalculator.Calculate(Input(new(2024, 3, 1), new(2026, 10, 31), unpaidFrom: new(2026, 10, 16), advance: 2000, notice: 500));

        r.ServiceDays.ShouldBe(975);
        r.EligibleForGratuity.ShouldBeTrue();
        r.GratuityAmount.ShouldBe(5609.59m);                 // 3000 / 30 × 21 × 975 / 365
        r.LeaveBalanceDays.ShouldBe(26.10m);                 // 21 × 975 / 365 = 56.10 accrued − 30 taken
        r.Lines.Single(l => l.Code == SettlementCalculator.LeaveEncashment).Amount.ShouldBe(2610.00m);
        r.UnpaidSalaryDays.ShouldBe(16);
        r.Lines.Single(l => l.Code == "SALARY_BASIC").Amount.ShouldBe(1600.00m);
        r.Lines.Single(l => l.Code == "SALARY_HOUSING").Amount.ShouldBe(533.33m);
        r.Lines.Single(l => l.Code == SettlementCalculator.AdvanceRecovery).Amount.ShouldBe(2000m);
        r.TotalEarnings.ShouldBe(5609.59m + 2610m + 1600m + 533.33m + 500m);
        r.NetPayable.ShouldBe(r.TotalEarnings - 2000m);
    }

    [Fact]
    public void Less_than_a_year_of_service_earns_no_gratuity()
    {
        var r = SettlementCalculator.Calculate(Input(new(2026, 3, 1), new(2026, 10, 31), leaveTaken: 0));
        r.EligibleForGratuity.ShouldBeFalse();
        r.GratuityAmount.ShouldBe(0);
        r.Lines.ShouldNotContain(l => l.Code == SettlementCalculator.Gratuity);
        r.Lines.ShouldContain(l => l.Code == SettlementCalculator.LeaveEncashment);
    }

    [Fact]
    public void Social_insurance_members_get_no_gratuity()
    {
        var r = SettlementCalculator.Calculate(Input(new(2020, 1, 1), new(2026, 10, 31), gratuityApplies: false));
        r.GratuityAmount.ShouldBe(0);
    }

    [Fact]
    public void Excess_leave_taken_is_recovered()
    {
        var r = SettlementCalculator.Calculate(Input(new(2026, 1, 1), new(2026, 6, 30), leaveTaken: 20));
        // accrued 21 × 181 / 365 = 10.41 → −9.59 days × 100/day
        r.LeaveBalanceDays.ShouldBe(-9.59m);
        r.Lines.Single(l => l.Code == SettlementCalculator.ExcessLeave).Amount.ShouldBe(959m);
    }

    [Fact]
    public void Salary_days_count_whole_months_as_thirty()
    {
        SettlementCalculator.SalaryDays(new(2026, 9, 15), new(2026, 11, 10)).ShouldBe(16 + 30 + 10);
        SettlementCalculator.SalaryDays(new(2026, 2, 1), new(2026, 2, 28)).ShouldBe(30);
        SettlementCalculator.SalaryDays(new(2026, 10, 1), new(2026, 10, 31)).ShouldBe(30);
        SettlementCalculator.SalaryDays(new(2026, 10, 31), new(2026, 10, 31)).ShouldBe(1);
    }

    [Fact]
    public void Negative_manual_amounts_are_rejected() =>
        Should.Throw<BusinessRuleException>(() => SettlementCalculator.Calculate(Input(new(2024, 1, 1), new(2026, 1, 1), notice: -5)));
}

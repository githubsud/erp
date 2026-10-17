using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;

namespace Erp.UnitTests;

public sealed class SocialInsuranceTests
{
    private static readonly PayrollPolicy Policy = new(30, 8, 1.25m, 1.5m, 1.5m, 1.5m, 50, 1000);
    private static readonly ComponentRef Basic = new(Guid.NewGuid(), "BASIC", "Basic", "أساسي", ComponentKind.Earning, WpsField.Basic, true);
    private static readonly ComponentRef Housing = new(Guid.NewGuid(), "HOUSING", "Housing", "سكن", ComponentKind.Earning, WpsField.Housing, true);
    private static readonly ComponentRef Si = new(Guid.NewGuid(), "SI_EMPLOYEE", "SI", "تأمينات", ComponentKind.Deduction, WpsField.Deduction, false, Statutory: true);
    private static readonly ComponentRef Fine = new(Guid.NewGuid(), "OTHER_DED", "Fine", "خصم", ComponentKind.Deduction, WpsField.Deduction, false);

    private static EmployeePayInput Input(decimal unpaid = 0, IReadOnlyList<VariablePay>? variable = null) =>
        new(new(2026, 10, 1), new(2026, 10, 31), new(2020, 1, 1), null,
            [new FixedPay(Basic, 15000), new FixedPay(Housing, 5000)], variable ?? [], [], unpaid,
            SocialInsurance: new SocialInsuranceContribution(Si, 20000, 7, 14));

    [Fact]
    public void Qatari_employee_pays_7_percent_and_employer_14_percent_of_the_contribution_salary()
    {
        var r = PayrollCalculator.Calculate(Input(), Policy);
        r.Lines.Single(l => l.Component.Code == "SI_EMPLOYEE").Amount.ShouldBe(1400m);
        r.EmployerSocialInsurance.ShouldBe(2800m);
        r.Net.ShouldBe(18600m);
    }

    [Fact]
    public void Contributions_follow_paid_days()
    {
        var r = PayrollCalculator.Calculate(Input(unpaid: 3), Policy); // 27/30 of 20,000 = 18,000
        r.Lines.Single(l => l.Component.Code == "SI_EMPLOYEE").Amount.ShouldBe(1260m);
        r.EmployerSocialInsurance.ShouldBe(2520m);
    }

    [Fact]
    public void Statutory_contributions_do_not_count_towards_the_deduction_cap()
    {
        // Fine of 9,900 + SI 1,400 = 11,300 > 50% of 20,000, but only the fine is capped.
        PayrollCalculator.Calculate(Input(variable: [new VariablePay(Fine, 9900)]), Policy).Errors.ShouldBeEmpty();
        PayrollCalculator.Calculate(Input(variable: [new VariablePay(Fine, 10100)]), Policy).Errors.ShouldNotBeEmpty();
    }
}

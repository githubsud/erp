using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.HR.Payroll;

public sealed record PayrollPolicy(
    decimal DailyRateDivisor,
    decimal HoursPerDay,
    decimal OvertimeNormalRate,
    decimal OvertimeNightRate,
    decimal OvertimeRestDayRate,
    decimal OvertimeHolidayRate,
    decimal DeductionCapPercent,
    decimal MinimumBasicWage)
{
    public static PayrollPolicy From(HrSettings s) => new(s.DailyRateDivisor, s.HoursPerDay, s.OvertimeNormalRate, s.OvertimeNightRate,
        s.OvertimeRestDayRate, s.OvertimeHolidayRate, s.DeductionCapPercent, s.MinimumBasicWage);

    public decimal Multiplier(OvertimeType type) => type switch
    {
        OvertimeType.Normal => OvertimeNormalRate,
        OvertimeType.Night => OvertimeNightRate,
        OvertimeType.RestDay => OvertimeRestDayRate,
        OvertimeType.Holiday => OvertimeHolidayRate,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

/// <summary>A pay component as the calculator sees it. Statutory deductions (social insurance) are outside the deduction cap.</summary>
public sealed record ComponentRef(Guid Id, string Code, string NameEn, string NameAr, ComponentKind Kind, WpsField WpsField, bool ProrateByDays,
    bool Statutory = false);

/// <summary>GRSIA contributions: percentages of the monthly contribution salary, prorated with paid days.</summary>
public sealed record SocialInsuranceContribution(ComponentRef EmployeeComponent, decimal MonthlyBase, decimal EmployeeRatePercent, decimal EmployerRatePercent);

public sealed record FixedPay(ComponentRef Component, decimal MonthlyAmount);

public sealed record VariablePay(ComponentRef Component, decimal Amount);

public sealed record OvertimeHours(ComponentRef Component, OvertimeType Type, decimal Hours);

public sealed record EmployeePayInput(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly JoiningDate,
    DateOnly? TerminationDate,
    IReadOnlyList<FixedPay> Fixed,
    IReadOnlyList<VariablePay> Variable,
    IReadOnlyList<OvertimeHours> Overtime,
    decimal UnpaidLeaveDays,
    decimal HalfPayLeaveDays = 0,
    SocialInsuranceContribution? SocialInsurance = null);

public sealed record PayLine(ComponentRef Component, decimal? Quantity, decimal? Rate, decimal Amount);

public sealed record PayResult(
    bool IsPayable,
    decimal PaidDays,
    decimal UnpaidDays,
    decimal BasicSalary,
    decimal HalfPayDays,
    decimal OvertimeHours,
    IReadOnlyList<PayLine> Lines,
    decimal Gross,
    decimal Deductions,
    decimal Net,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    decimal EmployerSocialInsurance = 0);

/// <summary>
/// Monthly pay for one employee on a 30-day basis (Qatar practice: daily rate = monthly / 30).
/// A full month is always 30 paid days; partial months count calendar days employed (capped at 30), less
/// approved unpaid leave. Overtime uses the basic hourly rate × the policy multiplier (Labour Law art. 73-75).
/// Every rule is driven by <see cref="PayrollPolicy"/>; this class has no I/O and is fully unit-tested.
/// </summary>
public static class PayrollCalculator
{
    public const decimal MonthBasisDays = 30m;

    public static PayResult Calculate(EmployeePayInput input, PayrollPolicy policy)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var start = input.JoiningDate > input.PeriodStart ? input.JoiningDate : input.PeriodStart;
        var end = input.TerminationDate is { } t && t < input.PeriodEnd ? t : input.PeriodEnd;
        if (end < start)
            return new PayResult(false, 0, 0, 0, 0, 0, [], 0, 0, 0, warnings, errors);

        var fullMonth = start == input.PeriodStart && end == input.PeriodEnd;
        var employedDays = fullMonth ? MonthBasisDays : Math.Min(MonthBasisDays, end.DayNumber - start.DayNumber + 1);
        var unpaid = Math.Min(employedDays, Math.Max(0, input.UnpaidLeaveDays));
        var halfPay = Math.Min(employedDays - unpaid, Math.Max(0, input.HalfPayLeaveDays));
        var paidDays = employedDays - unpaid - halfPay / 2;

        var basic = input.Fixed.Where(f => f.Component.WpsField == WpsField.Basic).Sum(f => f.MonthlyAmount);
        if (basic <= 0) errors.Add("No basic salary in the active contract.");
        else if (basic < policy.MinimumBasicWage) warnings.Add($"Basic salary {basic:N2} is below the legal minimum of {policy.MinimumBasicWage:N2}.");

        var lines = new List<PayLine>();
        foreach (var f in input.Fixed.Where(f => f.MonthlyAmount > 0))
        {
            var amount = f.Component.ProrateByDays && paidDays != MonthBasisDays
                ? Money.Round(f.MonthlyAmount * paidDays / MonthBasisDays)
                : f.MonthlyAmount;
            if (amount > 0) lines.Add(new PayLine(f.Component, f.Component.ProrateByDays ? paidDays : null, null, Money.Round(amount)));
        }

        var hourlyBasic = basic > 0 ? basic / (policy.DailyRateDivisor * policy.HoursPerDay) : 0;
        var overtimeHours = 0m;
        foreach (var group in input.Overtime.Where(o => o.Hours > 0).GroupBy(o => (o.Component.Id, o.Type)))
        {
            var hours = group.Sum(o => o.Hours);
            var rate = Math.Round(hourlyBasic * policy.Multiplier(group.Key.Type), 6, MidpointRounding.AwayFromZero);
            overtimeHours += hours;
            lines.Add(new PayLine(group.First().Component, hours, rate, Money.Round(hours * hourlyBasic * policy.Multiplier(group.Key.Type))));
        }

        foreach (var v in input.Variable.Where(v => v.Amount > 0))
            lines.Add(new PayLine(v.Component, null, null, Money.Round(v.Amount)));

        var employerSocialInsurance = 0m;
        if (input.SocialInsurance is { MonthlyBase: > 0 } si)
        {
            var contributionBase = paidDays == MonthBasisDays ? si.MonthlyBase : Money.Round(si.MonthlyBase * paidDays / MonthBasisDays);
            var employeeShare = Money.Round(contributionBase * si.EmployeeRatePercent / 100m);
            employerSocialInsurance = Money.Round(contributionBase * si.EmployerRatePercent / 100m);
            if (employeeShare > 0) lines.Add(new PayLine(si.EmployeeComponent, contributionBase, si.EmployeeRatePercent, employeeShare));
        }

        var gross = lines.Where(l => l.Component.Kind == ComponentKind.Earning).Sum(l => l.Amount);
        var deductions = lines.Where(l => l.Component.Kind == ComponentKind.Deduction).Sum(l => l.Amount);

        // The legal cap applies to deductions the employer chooses to make, not to statutory contributions.
        var cappedDeductions = lines.Where(l => l.Component.Kind == ComponentKind.Deduction && !l.Component.Statutory).Sum(l => l.Amount);
        var cap = Money.Round(gross * policy.DeductionCapPercent / 100m);
        if (cappedDeductions > cap)
            errors.Add($"Deductions {cappedDeductions:N2} exceed {policy.DeductionCapPercent:0.##}% of wages ({cap:N2}).");
        if (deductions > gross)
            errors.Add("Deductions exceed gross pay.");

        return new PayResult(true, paidDays, unpaid, basic, halfPay, overtimeHours, lines, gross, deductions, gross - deductions, warnings, errors,
            employerSocialInsurance);
    }

    /// <summary>Calendar days of <paramref name="from"/>..<paramref name="to"/> that fall inside the period.</summary>
    public static int OverlapDays(DateOnly from, DateOnly to, DateOnly periodStart, DateOnly periodEnd)
    {
        var s = from > periodStart ? from : periodStart;
        var e = to < periodEnd ? to : periodEnd;
        return e < s ? 0 : e.DayNumber - s.DayNumber + 1;
    }
}

/// <summary>
/// End-of-service gratuity (Labour Law art. 54): at least three weeks' basic wage per year of service, pro rata for
/// part years. The liability is recomputed in full every month so salary changes are trued up automatically.
/// </summary>
public static class GratuityCalculator
{
    public static int ServiceDays(DateOnly joiningDate, DateOnly asOf, int excludedDays) =>
        Math.Max(0, asOf.DayNumber - joiningDate.DayNumber + 1 - excludedDays);

    public static decimal Liability(decimal basisWage, decimal daysPerYear, decimal divisor, int serviceDays) =>
        serviceDays <= 0 || basisWage <= 0 ? 0 : Money.Round(basisWage * daysPerYear / divisor * serviceDays / 365m);

    public static bool IsEligibleForPayout(DateOnly joiningDate, DateOnly asOf, int minServiceMonths) =>
        joiningDate.AddMonths(minServiceMonths) <= asOf.AddDays(1);

    /// <summary>Annual leave entitlement (art. 79): 3 weeks below 5 years' service, 4 weeks from 5 years.</summary>
    public static int AnnualLeaveDaysPerYear(DateOnly joiningDate, DateOnly asOf, int? contractDays) =>
        contractDays ?? (joiningDate.AddYears(5) <= asOf ? 28 : 21);
}

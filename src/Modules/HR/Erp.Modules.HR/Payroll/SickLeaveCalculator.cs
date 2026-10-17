namespace Erp.Modules.HR.Payroll;

public sealed record SickLeaveAllocation(decimal FullPayDays, decimal HalfPayDays, decimal UnpaidDays);

/// <summary>
/// Sick leave pay (Labour Law art. 82, defaults from settings): once the employee has completed the minimum service
/// (3 months), the first 14 sick days in a service year are paid in full, the next 28 at half pay and the rest
/// unpaid. Days already taken earlier in the same service year count towards the bands.
/// </summary>
public static class SickLeaveCalculator
{
    public static SickLeaveAllocation Allocate(decimal priorDaysInServiceYear, decimal daysInPeriod, int fullPayBand, int halfPayBand, bool eligible)
    {
        if (daysInPeriod <= 0) return new(0, 0, 0);
        if (!eligible) return new(0, 0, daysInPeriod);

        var prior = Math.Max(0, priorDaysInServiceYear);
        var full = Math.Clamp(fullPayBand - prior, 0, daysInPeriod);
        var remaining = daysInPeriod - full;
        var halfAvailable = Math.Max(0, fullPayBand + halfPayBand - Math.Max(prior, fullPayBand));
        var half = Math.Min(remaining, halfAvailable);
        return new(full, half, remaining - half);
    }

    /// <summary>Start of the service year containing <paramref name="date"/> (the last joining anniversary on or before it).</summary>
    public static DateOnly ServiceYearStart(DateOnly joiningDate, DateOnly date)
    {
        if (date < joiningDate) return joiningDate;
        var years = date.Year - joiningDate.Year;
        var anniversary = joiningDate.AddYears(years);
        return anniversary > date ? joiningDate.AddYears(years - 1) : anniversary;
    }
}

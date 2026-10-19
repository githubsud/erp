namespace Erp.Modules.HR.Payroll;

public sealed record AttendanceHours(decimal Worked, decimal Normal, decimal Night, decimal RestDay, decimal Holiday)
{
    public decimal Overtime => Normal + Night + RestDay + Holiday;
}

/// <summary>
/// Splits one day's punches into overtime buckets (Labour Law art. 73–75). On a working day the hours beyond the
/// standard day are overtime; the part of it between 21:00 and 06:00 is night overtime. Every hour worked on the
/// weekly rest day (Friday) or a public holiday is overtime of that type. A punch-out earlier than the punch-in
/// is taken as the next morning (night shift). When the employee is on a roster, <c>isRestDay</c> says whether this is their
/// rostered day off; otherwise the company's weekly rest day applies.
/// </summary>
public static class AttendanceCalculator
{
    public const decimal MaxShiftHours = 18;
    private static readonly TimeOnly NightStart = new(21, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);

    public static AttendanceHours Calculate(DateOnly date, TimeOnly timeIn, TimeOnly timeOut, decimal hoursPerDay, bool isHoliday,
        DayOfWeek restDay = DayOfWeek.Friday, bool? isRestDay = null)
    {
        var start = date.ToDateTime(timeIn);
        var end = date.ToDateTime(timeOut);
        if (end <= start) end = end.AddDays(1);
        var worked = Hours(end - start);

        if (isHoliday) return new(worked, 0, 0, 0, worked);
        if (isRestDay ?? date.DayOfWeek == restDay) return new(worked, 0, 0, worked, 0);

        var overtime = Math.Max(0, worked - hoursPerDay);
        if (overtime == 0) return new(worked, 0, 0, 0, 0);

        // Overtime is the tail of the shift.
        var overtimeStart = end.AddHours(-(double)overtime);
        var night = TimeSpan.Zero;
        for (var day = date.AddDays(-1); day <= date.AddDays(1); day = day.AddDays(1))
        {
            var windowStart = day.ToDateTime(NightStart);
            var windowEnd = day.AddDays(1).ToDateTime(NightEnd);
            var from = overtimeStart > windowStart ? overtimeStart : windowStart;
            var to = end < windowEnd ? end : windowEnd;
            if (to > from) night += to - from;
        }
        var nightHours = Math.Min(overtime, Hours(night));
        return new(worked, overtime - nightHours, nightHours, 0, 0);
    }

    private static decimal Hours(TimeSpan span) => Math.Round((decimal)span.TotalMinutes / 60m, 2, MidpointRounding.AwayFromZero);
}

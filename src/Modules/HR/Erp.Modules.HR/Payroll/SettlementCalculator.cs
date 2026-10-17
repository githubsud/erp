using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.HR.Payroll;

public sealed record SettlementInput(
    DateOnly JoiningDate,
    DateOnly LastWorkingDay,
    int ExcludedServiceDays,
    bool GratuityApplies,
    decimal GratuityBasisWage,
    decimal GratuityDaysPerYear,
    decimal GratuityDivisor,
    int GratuityMinServiceMonths,
    decimal LeaveDaysPerYear,
    decimal LeaveDaysTaken,
    decimal LeaveBasisWage,
    decimal DailyRateDivisor,
    DateOnly? UnpaidSalaryFrom,
    decimal UnpaidLeaveDaysInWindow,
    IReadOnlyList<FixedPay> FixedPay,
    decimal NoticePay,
    decimal AirTicket,
    decimal OtherEarnings,
    decimal AdvanceRecovery,
    decimal OtherDeductions);

public sealed record SettlementLineResult(string Code, string NameEn, string NameAr, ComponentKind Kind, Guid? PayComponentId,
    decimal? Quantity, decimal? Rate, decimal Amount);

public sealed record SettlementResult(
    int ServiceDays,
    bool EligibleForGratuity,
    decimal GratuityAmount,
    decimal LeaveBalanceDays,
    decimal DailyLeaveRate,
    decimal UnpaidSalaryDays,
    IReadOnlyList<SettlementLineResult> Lines,
    decimal TotalEarnings,
    decimal TotalDeductions,
    decimal NetPayable);

/// <summary>
/// End-of-service dues (Labour Law art. 54 gratuity; art. 79 annual leave): gratuity is payable only after the minimum
/// service (one year by default) and is computed on the last wage; unused annual leave is encashed at the daily rate
/// (wage / 30), excess leave taken is recovered; salary not yet covered by a posted payroll is paid on the 30-day basis.
/// </summary>
public static class SettlementCalculator
{
    public const string Gratuity = "GRATUITY";
    public const string LeaveEncashment = "LEAVE_ENCASHMENT";
    public const string ExcessLeave = "EXCESS_LEAVE";
    public const string NoticePay = "NOTICE_PAY";
    public const string AirTicket = "AIR_TICKET";
    public const string OtherEarning = "OTHER_EARNING";
    public const string AdvanceRecovery = "ADVANCE_RECOVERY";
    public const string OtherDeduction = "OTHER_DEDUCTION";
    public const string SalaryPrefix = "SALARY_";

    public static SettlementResult Calculate(SettlementInput input)
    {
        var lines = new List<SettlementLineResult>();
        var serviceDays = GratuityCalculator.ServiceDays(input.JoiningDate, input.LastWorkingDay, input.ExcludedServiceDays);

        // Salary from the day after the last posted payroll to the last working day.
        var salaryDays = 0m;
        if (input.UnpaidSalaryFrom is { } from && from <= input.LastWorkingDay)
        {
            salaryDays = Math.Max(0, SalaryDays(from, input.LastWorkingDay) - input.UnpaidLeaveDaysInWindow);
            foreach (var f in input.FixedPay.Where(f => f.MonthlyAmount > 0))
            {
                var amount = Money.Round(f.MonthlyAmount * salaryDays / PayrollCalculator.MonthBasisDays);
                if (amount > 0)
                    lines.Add(new(SalaryPrefix + f.Component.Code, $"{f.Component.NameEn} (final month)", $"{f.Component.NameAr} (الشهر الأخير)",
                        ComponentKind.Earning, f.Component.Id, salaryDays, null, amount));
            }
        }

        var eligible = input.GratuityApplies
                       && GratuityCalculator.IsEligibleForPayout(input.JoiningDate, input.LastWorkingDay, input.GratuityMinServiceMonths);
        var gratuity = eligible
            ? GratuityCalculator.Liability(input.GratuityBasisWage, input.GratuityDaysPerYear, input.GratuityDivisor, serviceDays)
            : 0m;
        if (gratuity > 0)
            lines.Add(new(Gratuity, "End of service gratuity", "مكافأة نهاية الخدمة", ComponentKind.Earning, null, serviceDays, null, gratuity));

        var accruedLeave = Math.Round(input.LeaveDaysPerYear * serviceDays / 365m, 2, MidpointRounding.AwayFromZero);
        var leaveBalance = accruedLeave - input.LeaveDaysTaken;
        var dailyRate = input.DailyRateDivisor > 0 ? Math.Round(input.LeaveBasisWage / input.DailyRateDivisor, 6, MidpointRounding.AwayFromZero) : 0;
        if (leaveBalance > 0)
            lines.Add(new(LeaveEncashment, "Unused annual leave encashment", "بدل رصيد الإجازة السنوية", ComponentKind.Earning, null,
                leaveBalance, dailyRate, Money.Round(leaveBalance * input.LeaveBasisWage / input.DailyRateDivisor)));
        else if (leaveBalance < 0)
            lines.Add(new(ExcessLeave, "Recovery of excess leave taken", "استرداد إجازة زائدة", ComponentKind.Deduction, null,
                -leaveBalance, dailyRate, Money.Round(-leaveBalance * input.LeaveBasisWage / input.DailyRateDivisor)));

        AddManual(lines, NoticePay, "Notice period pay", "بدل فترة الإنذار", ComponentKind.Earning, input.NoticePay);
        AddManual(lines, AirTicket, "Return air ticket", "تذكرة السفر", ComponentKind.Earning, input.AirTicket);
        AddManual(lines, OtherEarning, "Other dues", "مستحقات أخرى", ComponentKind.Earning, input.OtherEarnings);
        AddManual(lines, AdvanceRecovery, "Recovery of salary advances / loans", "استرداد السلف والقروض", ComponentKind.Deduction, input.AdvanceRecovery);
        AddManual(lines, OtherDeduction, "Other deductions", "استقطاعات أخرى", ComponentKind.Deduction, input.OtherDeductions);

        var earnings = lines.Where(l => l.Kind == ComponentKind.Earning).Sum(l => l.Amount);
        var deductions = lines.Where(l => l.Kind == ComponentKind.Deduction).Sum(l => l.Amount);
        return new SettlementResult(serviceDays, eligible, gratuity, leaveBalance, dailyRate, salaryDays, lines, earnings, deductions, earnings - deductions);
    }

    /// <summary>Payable days between two dates on the 30-day basis: whole months count 30, partial months their calendar days.</summary>
    public static decimal SalaryDays(DateOnly from, DateOnly to)
    {
        var total = 0m;
        var cursor = from;
        while (cursor <= to)
        {
            var monthStart = new DateOnly(cursor.Year, cursor.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var segmentEnd = to < monthEnd ? to : monthEnd;
            var fullMonth = cursor == monthStart && segmentEnd == monthEnd;
            total += fullMonth ? PayrollCalculator.MonthBasisDays : Math.Min(PayrollCalculator.MonthBasisDays, segmentEnd.DayNumber - cursor.DayNumber + 1);
            cursor = segmentEnd.AddDays(1);
        }
        return total;
    }

    private static void AddManual(List<SettlementLineResult> lines, string code, string en, string ar, ComponentKind kind, decimal amount)
    {
        if (amount < 0)
            throw new BusinessRuleException("hr.settlement_negative", $"{en} cannot be negative.", $"لا يمكن أن تكون قيمة {ar} سالبة.");
        if (amount > 0) lines.Add(new(code, en, ar, kind, null, null, null, Money.Round(amount)));
    }
}

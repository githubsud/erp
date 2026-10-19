using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.HR.Rosters;

public sealed class WorkShift : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int BreakMinutes { get; set; }
    public int GraceMinutes { get; set; }
    public string? Color { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Paid hours: start to end (past midnight when the end is earlier) less the break.</summary>
    public decimal Hours => RosterRules.ShiftHours(StartTime, EndTime, BreakMinutes);
}

/// <summary>One employee's day on the roster. No shift = a rostered day off (the employee's rest day).</summary>
public sealed class RosterEntry : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public DateOnly WorkDate { get; init; }
    public Guid? ShiftId { get; set; }
}

internal sealed class WorkShiftConfiguration : IEntityTypeConfiguration<WorkShift>
{
    public void Configure(EntityTypeBuilder<WorkShift> b)
    {
        b.ToTable("work_shift", "hr");
        b.Ignore(x => x.Hours);
    }
}

internal sealed class RosterEntryConfiguration : IEntityTypeConfiguration<RosterEntry>
{
    public void Configure(EntityTypeBuilder<RosterEntry> b) => b.ToTable("roster_entry", "hr");
}

public static class RosterRules
{
    public static decimal ShiftHours(TimeOnly start, TimeOnly end, int breakMinutes)
    {
        var minutes = (end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;
        if (minutes <= 0) minutes += 24 * 60;
        return Math.Round((decimal)(minutes - breakMinutes) / 60m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Minutes late beyond the grace period, and minutes left before the shift end. Night shifts end the next morning.</summary>
    public static (int Late, int Early) Compare(DateOnly date, TimeOnly shiftStart, TimeOnly shiftEnd, int graceMinutes, TimeOnly timeIn, TimeOnly timeOut)
    {
        var start = date.ToDateTime(shiftStart);
        var end = date.ToDateTime(shiftEnd);
        if (end <= start) end = end.AddDays(1);
        var actualIn = date.ToDateTime(timeIn);
        // A punch-in well before midnight for an early-morning shift belongs to the evening before.
        if (actualIn - start > TimeSpan.FromHours(12)) actualIn = actualIn.AddDays(-1);
        var actualOut = date.ToDateTime(timeOut);
        if (actualOut <= actualIn) actualOut = actualOut.AddDays(1);
        var late = (int)Math.Max(0, (actualIn - start).TotalMinutes);
        var early = (int)Math.Max(0, (end - actualOut).TotalMinutes);
        return (late > graceMinutes ? late : 0, early);
    }
}

public sealed record ShiftRequest(string Code, string? NameEn, string? NameAr, TimeOnly StartTime, TimeOnly EndTime, int BreakMinutes = 0, int GraceMinutes = 0,
    string? Color = null, bool IsActive = true);

public sealed record ShiftDto(Guid Id, string Code, string NameEn, string NameAr, TimeOnly StartTime, TimeOnly EndTime, int BreakMinutes, int GraceMinutes,
    string? Color, bool IsActive, decimal Hours);

/// <summary>Sets one day. <see cref="Off"/> = rostered day off; neither shift nor off clears the day.</summary>
public sealed record RosterCell(Guid EmployeeId, DateOnly WorkDate, Guid? ShiftId, bool Off = false);

public sealed record AssignRequest(IReadOnlyList<Guid> EmployeeIds, DateOnly From, DateOnly To, Guid ShiftId, IReadOnlyList<DayOfWeek>? RestDays);

public sealed record CopyWeekRequest(DateOnly SourceWeekStart, DateOnly TargetWeekStart, IReadOnlyList<Guid>? EmployeeIds);

public sealed record RosterEmployee(Guid Id, string EmpNo, string NameEn, string NameAr, string? DepartmentEn, string? DepartmentAr);

public sealed record RosterView(DateOnly From, DateOnly To, IReadOnlyList<RosterEmployee> Employees, IReadOnlyList<RosterCell> Cells,
    IReadOnlyList<ShiftDto> Shifts);

public sealed record RosterException(Guid EmployeeId, DateOnly WorkDate, string Kind, string? ShiftCode, int Minutes);

public sealed record RosterReportRow(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, int RosteredDays, int WorkedDays, int OnLeaveDays,
    int AbsentDays, int LateCount, int LateMinutes, int EarlyCount, int EarlyMinutes, int UnrosteredDays, decimal RosteredHours, decimal WorkedHours);

public sealed record RosterReport(DateOnly From, DateOnly To, IReadOnlyList<RosterReportRow> Rows, IReadOnlyList<RosterException> Exceptions);

/// <summary>
/// Weekly shift rosters. The roster does not change pay by itself: it tells attendance which day is each employee's rest day
/// (overtime) and lets HR see absences, late arrivals and early leaving against the planned shift.
/// </summary>
internal sealed class RosterService(ErpDbContext db, ITenantContext tenant, IClock clock)
{
    public const int MaxDays = 62;

    public async Task<WorkShift> SaveShiftAsync(Guid? id, ShiftRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        if (r.BreakMinutes < 0 || r.GraceMinutes < 0 || RosterRules.ShiftHours(r.StartTime, r.EndTime, r.BreakMinutes) <= 0)
            throw new BusinessRuleException("hr.shift_times", "Check the shift times and break.", "تحقق من أوقات الوردية والاستراحة.");
        var companyId = tenant.RequireCompanyId();
        var shift = id is null
            ? db.Add(new WorkShift { CompanyId = companyId, Code = Guard.Required(r.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar }).Entity
            : await db.Set<WorkShift>().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == companyId, ct) ?? throw new NotFoundException("Shift", id);
        shift.NameEn = en;
        shift.NameAr = ar;
        shift.StartTime = r.StartTime;
        shift.EndTime = r.EndTime;
        shift.BreakMinutes = r.BreakMinutes;
        shift.GraceMinutes = r.GraceMinutes;
        shift.Color = string.IsNullOrWhiteSpace(r.Color) ? null : r.Color.Trim();
        shift.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return shift;
    }

    public async Task<List<ShiftDto>> ShiftsAsync(CancellationToken ct) =>
        (await db.Set<WorkShift>().AsNoTracking().Where(s => s.CompanyId == tenant.RequireCompanyId()).OrderBy(s => s.StartTime).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<RosterView> ViewAsync(DateOnly periodStart, DateOnly periodEnd, Guid? departmentId, CancellationToken ct)
    {
        CheckRange(periodStart, periodEnd);
        var employees = await EmployeesAsync(periodStart, periodEnd, departmentId, ct);
        var ids = employees.Select(e => e.Id).ToList();
        var cells = await db.Set<RosterEntry>().AsNoTracking()
            .Where(x => ids.Contains(x.EmployeeId) && x.WorkDate >= periodStart && x.WorkDate <= periodEnd)
            .Select(x => new RosterCell(x.EmployeeId, x.WorkDate, x.ShiftId, x.ShiftId == null)).ToListAsync(ct);
        return new RosterView(periodStart, periodEnd, employees, cells, await ShiftsAsync(ct));
    }

    public Task<int> SetAsync(IReadOnlyList<RosterCell> cells, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (cells.Count == 0) return 0;
            if (cells.Count > 5_000) throw new BusinessRuleException("hr.roster_too_many", "Too many days at once.", "عدد الأيام كبير جداً.");
            var employeeIds = cells.Select(c => c.EmployeeId).Distinct().ToList();
            await CheckEmployeesAsync(employeeIds, token);
            var shiftIds = cells.Where(c => c.ShiftId != null).Select(c => c.ShiftId!.Value).Distinct().ToList();
            var active = await db.Set<WorkShift>().Where(s => shiftIds.Contains(s.Id) && s.CompanyId == tenant.RequireCompanyId() && s.IsActive).Select(s => s.Id).ToListAsync(token);
            if (active.Count != shiftIds.Count) throw new NotFoundException("Shift", shiftIds.Except(active).First());
            var first = cells.Min(c => c.WorkDate);
            var last = cells.Max(c => c.WorkDate);
            var existing = (await db.Set<RosterEntry>().Where(x => employeeIds.Contains(x.EmployeeId) && x.WorkDate >= first && x.WorkDate <= last).ToListAsync(token))
                .ToDictionary(x => (x.EmployeeId, x.WorkDate));
            foreach (var c in cells.GroupBy(c => (c.EmployeeId, c.WorkDate)).Select(g => g.Last()))
            {
                existing.TryGetValue((c.EmployeeId, c.WorkDate), out var entry);
                if (c.ShiftId is null && !c.Off)
                {
                    if (entry is not null) db.Remove(entry);
                }
                else if (entry is null) db.Add(new RosterEntry { EmployeeId = c.EmployeeId, WorkDate = c.WorkDate, ShiftId = c.ShiftId });
                else entry.ShiftId = c.ShiftId;
            }
            await db.SaveChangesAsync(token);
            return cells.Count;
        }, ct);

    /// <summary>Fills a date range with one shift; the given weekdays become days off.</summary>
    public Task<int> AssignAsync(AssignRequest r, CancellationToken ct)
    {
        CheckRange(r.From, r.To);
        var rest = (r.RestDays ?? [DayOfWeek.Friday]).ToHashSet();
        var cells = new List<RosterCell>();
        foreach (var employeeId in r.EmployeeIds.Distinct())
            for (var d = r.From; d <= r.To; d = d.AddDays(1))
                cells.Add(rest.Contains(d.DayOfWeek) ? new RosterCell(employeeId, d, null, true) : new RosterCell(employeeId, d, r.ShiftId));
        return SetAsync(cells, ct);
    }

    /// <summary>Copies a week of the roster onto another week (days with nothing planned are left as they are).</summary>
    public async Task<int> CopyWeekAsync(CopyWeekRequest r, CancellationToken ct)
    {
        var offset = r.TargetWeekStart.DayNumber - r.SourceWeekStart.DayNumber;
        if (offset == 0) return 0;
        var sourceEnd = r.SourceWeekStart.AddDays(6);
        var companyId = tenant.RequireCompanyId();
        var q = from x in db.Set<RosterEntry>().AsNoTracking()
                join e in db.Set<Employee>() on x.EmployeeId equals e.Id
                where e.CompanyId == companyId && e.Status != EmployeeStatus.Terminated && x.WorkDate >= r.SourceWeekStart && x.WorkDate <= sourceEnd
                select x;
        if (r.EmployeeIds is { Count: > 0 }) q = q.Where(x => r.EmployeeIds.Contains(x.EmployeeId));
        var source = await q.ToListAsync(ct);
        return await SetAsync(source.Select(x => new RosterCell(x.EmployeeId, x.WorkDate.AddDays(offset), x.ShiftId, x.ShiftId == null)).ToList(), ct);
    }

    /// <summary>Roster against attendance and approved leave. Days after today are not counted as absences.</summary>
    public async Task<RosterReport> ReportAsync(DateOnly periodStart, DateOnly periodEnd, Guid? departmentId, CancellationToken ct)
    {
        CheckRange(periodStart, periodEnd);
        var employees = await EmployeesAsync(periodStart, periodEnd, departmentId, ct);
        var ids = employees.Select(e => e.Id).ToList();
        var shifts = (await db.Set<WorkShift>().AsNoTracking().Where(s => s.CompanyId == tenant.RequireCompanyId()).ToListAsync(ct)).ToDictionary(s => s.Id);
        var roster = (await db.Set<RosterEntry>().AsNoTracking().Where(x => ids.Contains(x.EmployeeId) && x.WorkDate >= periodStart && x.WorkDate <= periodEnd)
            .ToListAsync(ct)).ToDictionary(x => (x.EmployeeId, x.WorkDate));
        var attendance = (await db.Set<AttendanceDay>().AsNoTracking().Where(a => ids.Contains(a.EmployeeId) && a.WorkDate >= periodStart && a.WorkDate <= periodEnd)
            .ToListAsync(ct)).ToDictionary(a => (a.EmployeeId, a.WorkDate));
        var leave = await db.Set<LeaveRequest>().AsNoTracking()
            .Where(l => ids.Contains(l.EmployeeId) && l.Status == LeaveStatus.Approved && l.FromDate <= periodEnd && l.ToDate >= periodStart).ToListAsync(ct);
        var today = clock.Today;

        var rows = new List<RosterReportRow>();
        var exceptions = new List<RosterException>();
        foreach (var e in employees)
        {
            int rostered = 0, worked = 0, onLeave = 0, absent = 0, lateCount = 0, lateMinutes = 0, earlyCount = 0, earlyMinutes = 0, unrostered = 0;
            decimal rosteredHours = 0, workedHours = 0;
            for (var d = periodStart; d <= periodEnd; d = d.AddDays(1))
            {
                roster.TryGetValue((e.Id, d), out var entry);
                attendance.TryGetValue((e.Id, d), out var day);
                var shift = entry?.ShiftId is { } sid ? shifts[sid] : null;
                if (day is not null)
                {
                    worked++;
                    workedHours += day.WorkedHours;
                }
                if (shift is null)
                {
                    if (day is not null && entry is null) unrostered++;
                    continue;
                }
                rostered++;
                rosteredHours += shift.Hours;
                if (day is not null)
                {
                    var (late, early) = RosterRules.Compare(d, shift.StartTime, shift.EndTime, shift.GraceMinutes, day.TimeIn, day.TimeOut);
                    if (late > 0)
                    {
                        lateCount++;
                        lateMinutes += late;
                        exceptions.Add(new RosterException(e.Id, d, "Late", shift.Code, late));
                    }
                    if (early > 0)
                    {
                        earlyCount++;
                        earlyMinutes += early;
                        exceptions.Add(new RosterException(e.Id, d, "Early", shift.Code, early));
                    }
                }
                else if (leave.Any(l => l.EmployeeId == e.Id && l.FromDate <= d && l.ToDate >= d)) onLeave++;
                else if (d <= today)
                {
                    absent++;
                    exceptions.Add(new RosterException(e.Id, d, "Absent", shift.Code, 0));
                }
            }
            rows.Add(new RosterReportRow(e.Id, e.EmpNo, e.NameEn, e.NameAr, rostered, worked, onLeave, absent, lateCount, lateMinutes, earlyCount, earlyMinutes,
                unrostered, rosteredHours, workedHours));
        }
        return new RosterReport(periodStart, periodEnd, rows, exceptions.OrderBy(x => x.WorkDate).ToList());
    }

    /// <summary>Employees with a rostered day off on these dates, for attendance overtime (their rest day instead of Friday).</summary>
    public static async Task<Dictionary<(Guid EmployeeId, DateOnly Date), bool>> RestDaysAsync(ErpDbContext db, IReadOnlyCollection<Guid> employeeIds, DateOnly first,
        DateOnly last, CancellationToken ct) =>
        await db.Set<RosterEntry>().AsNoTracking().Where(x => employeeIds.Contains(x.EmployeeId) && x.WorkDate >= first && x.WorkDate <= last)
            .ToDictionaryAsync(x => (x.EmployeeId, x.WorkDate), x => x.ShiftId == null, ct);

    private async Task<List<RosterEmployee>> EmployeesAsync(DateOnly periodStart, DateOnly periodEnd, Guid? departmentId, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        return await (from e in db.Set<Employee>().AsNoTracking()
                      join dep in db.Set<Department>() on e.DepartmentId equals dep.Id into deps
                      from dep in deps.DefaultIfEmpty()
                      where e.CompanyId == companyId && e.JoiningDate <= periodEnd && (e.Status != EmployeeStatus.Terminated || e.TerminationDate >= periodStart)
                            && (departmentId == null || e.DepartmentId == departmentId)
                      orderby e.EmpNo
                      select new RosterEmployee(e.Id, e.EmpNo, e.NameEn, e.NameAr, dep == null ? null : dep.NameEn, dep == null ? null : dep.NameAr)).ToListAsync(ct);
    }

    private async Task CheckEmployeesAsync(List<Guid> ids, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var found = await db.Set<Employee>().Where(e => ids.Contains(e.Id) && e.CompanyId == companyId).Select(e => e.Id).ToListAsync(ct);
        if (found.Count != ids.Count) throw new NotFoundException("Employee", ids.Except(found).First());
    }

    private static void CheckRange(DateOnly periodStart, DateOnly periodEnd)
    {
        if (periodEnd < periodStart || periodEnd.DayNumber - periodStart.DayNumber >= MaxDays)
            throw new BusinessRuleException("hr.roster_range", $"Choose a range of up to {MaxDays} days.", $"اختر فترة لا تتجاوز {MaxDays} يوماً.");
    }

    private static ShiftDto ToDto(WorkShift s) => new(s.Id, s.Code, s.NameEn, s.NameAr, s.StartTime, s.EndTime, s.BreakMinutes, s.GraceMinutes, s.Color, s.IsActive, s.Hours);

    internal static ShiftDto Dto(WorkShift s) => ToDto(s);
}

internal static class RosterEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hr").WithTags("HR & Payroll").RequireAuthorization();
        g.MapGet("/shifts", (RosterService s, CancellationToken ct) => s.ShiftsAsync(ct)).RequirePermission(HrPermissions.EmployeesRead);
        g.MapPost("/shifts", async (ShiftRequest r, RosterService s, CancellationToken ct) => RosterService.Dto(await s.SaveShiftAsync(null, r, ct)))
            .RequirePermission(HrPermissions.RostersManage);
        g.MapPut("/shifts/{id:guid}", async (Guid id, ShiftRequest r, RosterService s, CancellationToken ct) => RosterService.Dto(await s.SaveShiftAsync(id, r, ct)))
            .RequirePermission(HrPermissions.RostersManage);

        g.MapGet("/rosters", (DateOnly from, DateOnly to, RosterService s, CancellationToken ct, Guid? departmentId = null) => s.ViewAsync(from, to, departmentId, ct))
            .RequirePermission(HrPermissions.EmployeesRead);
        g.MapPut("/rosters", async (List<RosterCell> cells, RosterService s, CancellationToken ct) => new { Saved = await s.SetAsync(cells, ct) })
            .RequirePermission(HrPermissions.RostersManage);
        g.MapPost("/rosters/assign", async (AssignRequest r, RosterService s, CancellationToken ct) => new { Saved = await s.AssignAsync(r, ct) })
            .RequirePermission(HrPermissions.RostersManage);
        g.MapPost("/rosters/copy-week", async (CopyWeekRequest r, RosterService s, CancellationToken ct) => new { Saved = await s.CopyWeekAsync(r, ct) })
            .RequirePermission(HrPermissions.RostersManage);
        g.MapGet("/rosters/report", (DateOnly from, DateOnly to, RosterService s, CancellationToken ct, Guid? departmentId = null) =>
            s.ReportAsync(from, to, departmentId, ct)).RequirePermission(HrPermissions.EmployeesRead);
    }
}

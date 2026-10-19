using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Import;
using Erp.Modules.HR.Payroll;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.HR.SelfService;

public enum ExpenseCategory { Fuel, Meals, Transport, Accommodation, Supplies, Phone, Other }

public enum ExpenseStatus { Submitted, Approved, Rejected, Cancelled }

public enum PunchKind { In, Out }

public sealed class ExpenseClaim : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid EmployeeId { get; init; }
    public required string ClaimNo { get; init; }
    public DateOnly ExpenseDate { get; init; }
    public ExpenseCategory Category { get; init; }
    public required string Description { get; init; }
    public decimal Amount { get; init; }
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Submitted;
    public byte[]? Receipt { get; init; }
    public string? ReceiptContentType { get; init; }
    public string? ReceiptFileName { get; init; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public int? PayrollYear { get; set; }
    public int? PayrollMonth { get; set; }
    public Guid? PayrollInputId { get; set; }
}

public sealed class WorkSite : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int RadiusM { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AttendancePunch : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public DateTimeOffset PunchedAt { get; init; }
    public PunchKind Kind { get; init; }
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int? AccuracyM { get; init; }
    public Guid? SiteId { get; init; }
    public int? DistanceM { get; init; }
    public bool Accepted { get; init; }
}

internal sealed class ExpenseClaimConfiguration : IEntityTypeConfiguration<ExpenseClaim>
{
    public void Configure(EntityTypeBuilder<ExpenseClaim> b) => b.ToTable("expense_claim", "hr");
}

internal sealed class WorkSiteConfiguration : IEntityTypeConfiguration<WorkSite>
{
    public void Configure(EntityTypeBuilder<WorkSite> b)
    {
        b.ToTable("work_site", "hr");
        b.Property(x => x.Latitude).HasPrecision(9, 6);
        b.Property(x => x.Longitude).HasPrecision(9, 6);
    }
}

internal sealed class AttendancePunchConfiguration : IEntityTypeConfiguration<AttendancePunch>
{
    public void Configure(EntityTypeBuilder<AttendancePunch> b)
    {
        b.ToTable("attendance_punch", "hr");
        b.Property(x => x.Latitude).HasPrecision(9, 6);
        b.Property(x => x.Longitude).HasPrecision(9, 6);
    }
}

public static class Geo
{
    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

public sealed record ExpenseDto(Guid Id, string ClaimNo, Guid EmployeeId, string EmpNo, string NameEn, string NameAr, DateOnly ExpenseDate, string Category,
    string Description, decimal Amount, string Status, bool HasReceipt, DateTimeOffset? DecidedAt, string? DecisionNote, int? PayrollYear, int? PayrollMonth);

public sealed record ExpenseDecision(bool Approve, string? Note);

public sealed record SiteRequest(string Code, string? NameEn, string? NameAr, decimal Latitude, decimal Longitude, int RadiusM, bool IsActive = true);

public sealed record PunchRequest(PunchKind Kind, decimal Latitude, decimal Longitude, int? AccuracyM);

public sealed record PunchResult(bool Accepted, string Kind, DateTimeOffset PunchedAt, string? SiteCode, string? SiteNameEn, string? SiteNameAr, int? DistanceM,
    decimal? WorkedHours);

public sealed record PunchDto(Guid Id, Guid EmployeeId, string EmpNo, string NameEn, string NameAr, DateTimeOffset PunchedAt, string Kind, decimal Latitude,
    decimal Longitude, int? AccuracyM, string? SiteCode, int? DistanceM, bool Accepted);

/// <summary>
/// Field staff self-service. Expense claims are approved by HR and paid through the next open payroll as a reimbursement (so they
/// reach the employee by WPS). Punches are accepted only inside a work site's radius; punching out builds that day's attendance
/// (and its overtime) exactly as an imported day would.
/// </summary>
internal sealed class FieldStaffService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IClock clock, HrSettingsProvider settings,
    AttendanceImportService attendance)
{
    public const int MaxReceiptBytes = 3 * 1024 * 1024;
    private static readonly string[] ReceiptTypes = ["image/jpeg", "image/png", "image/webp", "application/pdf"];

    public Task<ExpenseClaim> SubmitAsync(Employee me, DateOnly date, ExpenseCategory category, string? description, decimal amount, IFormFile? receipt,
        CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (amount <= 0) throw new BusinessRuleException("hr.expense_amount", "Enter the amount spent.", "أدخل المبلغ المصروف.");
            if (date > clock.Today || date < clock.Today.AddDays(-90))
                throw new BusinessRuleException("hr.expense_date", "Claims are for expenses in the last 90 days.", "تُقدم المطالبات عن مصروفات آخر 90 يوماً.");
            byte[]? content = null;
            if (receipt is not null)
            {
                if (receipt.Length > MaxReceiptBytes || !ReceiptTypes.Contains(receipt.ContentType))
                    throw new BusinessRuleException("hr.expense_receipt", "Attach a photo or PDF of up to 3 MB.", "أرفق صورة أو ملف PDF حتى 3 ميغابايت.");
                using var ms = new MemoryStream();
                await receipt.CopyToAsync(ms, token);
                content = ms.ToArray();
            }
            var claim = new ExpenseClaim
            {
                CompanyId = me.CompanyId, EmployeeId = me.Id, ClaimNo = await numbering.NextAsync("expense_claim", "EXP", date, ct: token), ExpenseDate = date,
                Category = category, Description = Guard.Required(description, "description").Trim()[..Math.Min(description!.Trim().Length, 500)], Amount = Money.Round(amount),
                Receipt = content, ReceiptContentType = receipt?.ContentType, ReceiptFileName = receipt is null ? null : Path.GetFileName(receipt.FileName)
            };
            db.Add(claim);
            await db.SaveChangesAsync(token);
            return claim;
        }, ct);

    public async Task CancelAsync(Employee me, Guid id, CancellationToken ct)
    {
        var claim = await db.Set<ExpenseClaim>().SingleOrDefaultAsync(c => c.Id == id && c.EmployeeId == me.Id, ct) ?? throw new NotFoundException("Expense claim", id);
        if (claim.Status != ExpenseStatus.Submitted) throw new ConflictException("hr.expense_decided", "This claim has already been decided.", "تم البت في هذه المطالبة.");
        claim.Status = ExpenseStatus.Cancelled;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Approval puts the amount on the first payroll month that is still open (this month or later).</summary>
    public Task<ExpenseClaim> DecideAsync(Guid id, ExpenseDecision r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var claim = await db.Set<ExpenseClaim>().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId, token) ?? throw new NotFoundException("Expense claim", id);
            if (claim.Status != ExpenseStatus.Submitted)
                throw new ConflictException("hr.expense_decided", "This claim has already been decided.", "تم البت في هذه المطالبة.");
            if (!r.Approve && string.IsNullOrWhiteSpace(r.Note))
                throw new BusinessRuleException("hr.expense_reason", "Give a reason for rejecting.", "اذكر سبب الرفض.");
            claim.DecidedBy = tenant.ActingUserId;
            claim.DecidedAt = clock.UtcNow;
            claim.DecisionNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
            if (!r.Approve)
            {
                claim.Status = ExpenseStatus.Rejected;
                await db.SaveChangesAsync(token);
                return claim;
            }
            var component = await db.Set<PayComponent>().SingleOrDefaultAsync(c => c.CompanyId == companyId && c.Code == "EXPENSE_CLAIM" && c.IsActive, token)
                            ?? throw new BusinessRuleException("hr.expense_component", "The expense reimbursement pay component is missing.",
                                "مكون تعويض المصروفات غير موجود.");
            var locked = (await db.Set<PayrollRun>().AsNoTracking()
                    .Where(x => x.CompanyId == companyId && (x.Status == PayrollStatus.Approved || x.Status == PayrollStatus.Posted || x.Status == PayrollStatus.Paid))
                    .Select(x => new { x.Year, x.Month }).ToListAsync(token))
                .Select(x => x.Year * 12 + x.Month).ToHashSet();
            var period = clock.Today.Year * 12 + clock.Today.Month;
            while (locked.Contains(period)) period++;
            var (year, month) = ((period - 1) / 12, (period - 1) % 12 + 1);
            var input = new PayrollInput
            {
                EmployeeId = claim.EmployeeId, Year = year, Month = month, PayComponentId = component.Id, Amount = claim.Amount, Source = InputSource.Expense,
                Notes = $"Expense claim {claim.ClaimNo} / مطالبة مصروفات"
            };
            db.Add(input);
            claim.Status = ExpenseStatus.Approved;
            claim.PayrollYear = year;
            claim.PayrollMonth = month;
            claim.PayrollInputId = input.Id;
            await db.SaveChangesAsync(token);
            return claim;
        }, ct);

    public async Task<List<ExpenseDto>> ListAsync(Guid? employeeId, ExpenseStatus? status, CancellationToken ct) =>
        await (from c in db.Set<ExpenseClaim>().AsNoTracking()
               join e in db.Set<Employee>() on c.EmployeeId equals e.Id
               where c.CompanyId == tenant.RequireCompanyId() && (employeeId == null || c.EmployeeId == employeeId) && (status == null || c.Status == status)
               orderby c.CreatedAt descending
               select new ExpenseDto(c.Id, c.ClaimNo, c.EmployeeId, e.EmpNo, e.NameEn, e.NameAr, c.ExpenseDate, c.Category.ToString(), c.Description, c.Amount,
                   c.Status.ToString(), c.Receipt != null, c.DecidedAt, c.DecisionNote, c.PayrollYear, c.PayrollMonth)).Take(300).ToListAsync(ct);

    public async Task<WorkSite> SaveSiteAsync(Guid? id, SiteRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        if (r.Latitude is < -90 or > 90 || r.Longitude is < -180 or > 180 || r.RadiusM is < 20 or > 5000)
            throw new BusinessRuleException("hr.site_values", "Check the coordinates and a radius of 20–5000 m.", "تحقق من الإحداثيات ونصف قطر بين 20 و5000 متر.");
        var companyId = tenant.RequireCompanyId();
        var site = id is null
            ? db.Add(new WorkSite { CompanyId = companyId, Code = Guard.Required(r.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar }).Entity
            : await db.Set<WorkSite>().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == companyId, ct) ?? throw new NotFoundException("Work site", id);
        site.NameEn = en;
        site.NameAr = ar;
        site.Latitude = Math.Round(r.Latitude, 6);
        site.Longitude = Math.Round(r.Longitude, 6);
        site.RadiusM = r.RadiusM;
        site.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return site;
    }

    /// <summary>Every attempt is kept (also refused ones, for review); only accepted punches count.</summary>
    public Task<PunchResult> PunchAsync(Employee me, PunchRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var sites = await db.Set<WorkSite>().AsNoTracking().Where(s => s.CompanyId == me.CompanyId && s.IsActive).ToListAsync(token);
            if (sites.Count == 0)
                throw new BusinessRuleException("hr.no_sites", "No work sites are set up for phone attendance.", "لم يتم إعداد مواقع عمل لتسجيل الحضور بالهاتف.");
            var nearest = sites.Select(s => (Site: s, Distance: Geo.DistanceM((double)r.Latitude, (double)r.Longitude, (double)s.Latitude, (double)s.Longitude)))
                .OrderBy(x => x.Distance - x.Site.RadiusM).First();
            var accepted = nearest.Distance <= nearest.Site.RadiusM;
            var now = clock.UtcNow;
            var punch = new AttendancePunch
            {
                EmployeeId = me.Id, PunchedAt = now, Kind = r.Kind, Latitude = Math.Round(r.Latitude, 6), Longitude = Math.Round(r.Longitude, 6),
                AccuracyM = r.AccuracyM, SiteId = nearest.Site.Id, DistanceM = (int)Math.Round(nearest.Distance), Accepted = accepted
            };
            db.Add(punch);
            await db.SaveChangesAsync(token);
            if (!accepted)
                return new PunchResult(false, r.Kind.ToString(), now, nearest.Site.Code, nearest.Site.NameEn, nearest.Site.NameAr, punch.DistanceM, null);

            decimal? worked = null;
            if (r.Kind == PunchKind.Out)
            {
                var punchIn = await db.Set<AttendancePunch>().AsNoTracking()
                    .Where(p => p.EmployeeId == me.Id && p.Accepted && p.Kind == PunchKind.In && p.PunchedAt < now && p.PunchedAt > now.AddHours(-(double)AttendanceCalculator.MaxShiftHours))
                    .OrderByDescending(p => p.PunchedAt).FirstOrDefaultAsync(token)
                    ?? throw new BusinessRuleException("hr.no_punch_in", "Punch in first.", "سجّل الحضور أولاً.");
                worked = await RecordDayAsync(me, punchIn.PunchedAt, now, token);
            }
            return new PunchResult(true, r.Kind.ToString(), now, nearest.Site.Code, nearest.Site.NameEn, nearest.Site.NameAr, punch.DistanceM, worked);
        }, ct);

    /// <summary>The day of the punch-in (Qatar time) gets these times; overtime inputs for the month are rebuilt.</summary>
    private async Task<decimal> RecordDayAsync(Employee me, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var qatar = TimeSpan.FromHours(3);
        var date = DateOnly.FromDateTime(start.ToOffset(qatar).DateTime);
        var timeIn = TimeOnly.FromDateTime(start.ToOffset(qatar).DateTime);
        var timeOut = TimeOnly.FromDateTime(end.ToOffset(qatar).DateTime);
        if (await db.Set<PayrollRun>().AnyAsync(x => x.CompanyId == me.CompanyId && x.Year == date.Year && x.Month == date.Month
                                                     && (x.Status == PayrollStatus.Approved || x.Status == PayrollStatus.Posted || x.Status == PayrollStatus.Paid), ct))
            throw new BusinessRuleException("hr.payroll_locked", $"Payroll for {date:yyyy-MM} is already approved.", $"تم اعتماد رواتب {date:yyyy-MM} مسبقاً.");
        var restDays = await Rosters.RosterService.RestDaysAsync(db, [me.Id], date, date, ct);
        bool? rest = restDays.TryGetValue((me.Id, date), out var off) ? off : null;
        var hours = AttendanceCalculator.Calculate(date, timeIn, timeOut, (await settings.GetAsync(ct)).HoursPerDay, false, isRestDay: rest);
        var existing = await db.Set<AttendanceDay>().SingleOrDefaultAsync(a => a.EmployeeId == me.Id && a.WorkDate == date, ct);
        if (existing is not null)
        {
            // A second shift the same day: keep the first punch-in and extend to this punch-out.
            timeIn = existing.TimeIn < timeIn ? existing.TimeIn : timeIn;
            hours = AttendanceCalculator.Calculate(date, timeIn, timeOut, (await settings.GetAsync(ct)).HoursPerDay, existing.IsHoliday, isRestDay: rest);
            db.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
        db.Add(new AttendanceDay
        {
            EmployeeId = me.Id, WorkDate = date, TimeIn = timeIn, TimeOut = timeOut, IsHoliday = existing?.IsHoliday ?? false, WorkedHours = hours.Worked,
            NormalOvertime = hours.Normal, NightOvertime = hours.Night, RestDayOvertime = hours.RestDay, HolidayOvertime = hours.Holiday
        });
        await db.SaveChangesAsync(ct);
        await attendance.RebuildOvertimeInputsAsync(me.CompanyId, [me.Id], date.Year, date.Month, ct);
        return hours.Worked;
    }

    public async Task<List<PunchDto>> PunchesAsync(DateOnly periodStart, DateOnly periodEnd, Guid? employeeId, CancellationToken ct)
    {
        // Qatar days, compared in UTC (the column type only accepts UTC values).
        var start = new DateTimeOffset(periodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
        var end = new DateTimeOffset(periodEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
        return await (from p in db.Set<AttendancePunch>().AsNoTracking()
                      join e in db.Set<Employee>() on p.EmployeeId equals e.Id
                      join s in db.Set<WorkSite>() on p.SiteId equals s.Id into ss
                      from s in ss.DefaultIfEmpty()
                      where e.CompanyId == tenant.RequireCompanyId() && p.PunchedAt >= start && p.PunchedAt < end && (employeeId == null || p.EmployeeId == employeeId)
                      orderby p.PunchedAt descending
                      select new PunchDto(p.Id, e.Id, e.EmpNo, e.NameEn, e.NameAr, p.PunchedAt, p.Kind.ToString(), p.Latitude, p.Longitude, p.AccuracyM,
                          s == null ? null : s.Code, p.DistanceM, p.Accepted)).Take(1000).ToListAsync(ct);
    }
}

internal static class FieldStaffEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").WithTags("Employee self-service").RequireAuthorization().RequirePermission(HrPermissions.SelfService);
        me.MapGet("/expenses", async (ErpDbContext db, ITenantContext t, FieldStaffService s, CancellationToken ct) =>
            await s.ListAsync((await SelfServiceEndpoints.MeAsync(db, t, ct)).Id, null, ct));
        me.MapPost("/expenses", async (HttpRequest req, ErpDbContext db, ITenantContext t, FieldStaffService s, CancellationToken ct) =>
        {
            var form = await req.ReadFormAsync(ct);
            var employee = await SelfServiceEndpoints.MeAsync(db, t, ct);
            if (!DateOnly.TryParse(form["expenseDate"], out var date) || !Enum.TryParse<ExpenseCategory>(form["category"], true, out var category)
                || !decimal.TryParse(form["amount"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                throw new BusinessRuleException("hr.expense_form", "Enter the date, category and amount.", "أدخل التاريخ والفئة والمبلغ.");
            var claim = await s.SubmitAsync(employee, date, category, form["description"], amount, form.Files.GetFile("receipt"), ct);
            return new { claim.Id, claim.ClaimNo };
        }).DisableAntiforgery();
        me.MapPost("/expenses/{id:guid}/cancel", async (Guid id, ErpDbContext db, ITenantContext t, FieldStaffService s, CancellationToken ct) =>
        {
            await s.CancelAsync(await SelfServiceEndpoints.MeAsync(db, t, ct), id, ct);
            return Results.NoContent();
        });
        me.MapGet("/expenses/{id:guid}/receipt", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var employee = await SelfServiceEndpoints.MeAsync(db, t, ct);
            return await ReceiptAsync(db, id, employee.Id, ct);
        });
        me.MapGet("/sites", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await db.Set<WorkSite>().AsNoTracking().Where(s => s.CompanyId == t.RequireCompanyId() && s.IsActive).OrderBy(s => s.Code)
                .Select(s => new { s.Id, s.Code, s.NameEn, s.NameAr, s.RadiusM }).ToListAsync(ct));
        me.MapPost("/punch", async (PunchRequest r, ErpDbContext db, ITenantContext t, FieldStaffService s, CancellationToken ct) =>
            await s.PunchAsync(await SelfServiceEndpoints.MeAsync(db, t, ct), r, ct));
        me.MapGet("/punches", async (ErpDbContext db, ITenantContext t, FieldStaffService s, IClock clock, CancellationToken ct) =>
            await s.PunchesAsync(clock.Today.AddDays(-6), clock.Today, (await SelfServiceEndpoints.MeAsync(db, t, ct)).Id, ct));

        var hr = app.MapGroup("/api/hr").WithTags("HR & Payroll").RequireAuthorization();
        hr.MapGet("/expenses", (FieldStaffService s, CancellationToken ct, ExpenseStatus? status = null, Guid? employeeId = null) => s.ListAsync(employeeId, status, ct))
            .RequirePermission(HrPermissions.EmployeesRead);
        hr.MapPost("/expenses/{id:guid}/decide", async (Guid id, ExpenseDecision r, FieldStaffService s, CancellationToken ct) =>
        {
            var c = await s.DecideAsync(id, r, ct);
            return new { c.Id, Status = c.Status.ToString(), c.PayrollYear, c.PayrollMonth };
        }).RequirePermission(HrPermissions.ExpensesApprove);
        hr.MapGet("/expenses/{id:guid}/receipt", (Guid id, ErpDbContext db, CancellationToken ct) => ReceiptAsync(db, id, null, ct))
            .RequirePermission(HrPermissions.EmployeesRead);
        hr.MapGet("/work-sites", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await db.Set<WorkSite>().AsNoTracking().Where(s => s.CompanyId == t.RequireCompanyId()).OrderBy(s => s.Code)
                .Select(s => new { s.Id, s.Code, s.NameEn, s.NameAr, s.Latitude, s.Longitude, s.RadiusM, s.IsActive }).ToListAsync(ct))
            .RequirePermission(HrPermissions.EmployeesRead);
        hr.MapPost("/work-sites", async (SiteRequest r, FieldStaffService s, CancellationToken ct) => (await s.SaveSiteAsync(null, r, ct)).Id)
            .RequirePermission(HrPermissions.SettingsManage);
        hr.MapPut("/work-sites/{id:guid}", async (Guid id, SiteRequest r, FieldStaffService s, CancellationToken ct) => (await s.SaveSiteAsync(id, r, ct)).Id)
            .RequirePermission(HrPermissions.SettingsManage);
        hr.MapGet("/punches", (DateOnly from, DateOnly to, FieldStaffService s, CancellationToken ct, Guid? employeeId = null) =>
            s.PunchesAsync(from, to, employeeId, ct)).RequirePermission(HrPermissions.PayrollRead);
    }

    private static async Task<IResult> ReceiptAsync(ErpDbContext db, Guid id, Guid? employeeId, CancellationToken ct)
    {
        var claim = await db.Set<ExpenseClaim>().AsNoTracking().Where(c => c.Id == id && (employeeId == null || c.EmployeeId == employeeId))
                        .Select(c => new { c.Receipt, c.ReceiptContentType, c.ReceiptFileName }).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Expense claim", id);
        if (claim.Receipt is null) throw new NotFoundException("Receipt", id);
        return Results.File(claim.Receipt, claim.ReceiptContentType ?? "application/octet-stream", claim.ReceiptFileName ?? "receipt");
    }
}

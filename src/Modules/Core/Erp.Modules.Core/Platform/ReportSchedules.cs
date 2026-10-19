using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Core.Platform;

public enum ReportFrequency { Daily, Weekly, Monthly }

public sealed class ReportSchedule : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string ReportKey { get; set; }
    public ReportFrequency Frequency { get; set; }
    public int? Weekday { get; set; }
    public int? MonthDay { get; set; }
    public required string Recipients { get; set; }
    public Guid OwnerUserId { get; init; }
    public bool IsActive { get; set; } = true;
    public DateOnly NextRunOn { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> b) => b.ToTable("report_schedule", "core");
}

public sealed record ReportScheduleRequest(string ReportKey, ReportFrequency Frequency, int? Weekday, int? MonthDay, IReadOnlyList<string> Recipients,
    bool IsActive = true);

public sealed record ReportScheduleDto(Guid Id, string ReportKey, string ReportNameEn, string ReportNameAr, string Frequency, int? Weekday, int? MonthDay,
    IReadOnlyList<string> Recipients, string OwnerName, bool IsActive, DateOnly NextRunOn, DateTimeOffset? LastSentAt, string? LastError);

public sealed record ScheduledReportInfo(string Key, string NameEn, string NameAr);

public static class ReportScheduleRules
{
    /// <summary>The first run date strictly after <paramref name="after"/> (or on it when <paramref name="inclusive"/>).</summary>
    public static DateOnly NextRun(ReportFrequency frequency, int? weekday, int? monthDay, DateOnly after, bool inclusive = false)
    {
        var d = inclusive ? after : after.AddDays(1);
        return frequency switch
        {
            ReportFrequency.Daily => d,
            ReportFrequency.Weekly => d.AddDays(((weekday ?? 0) - (int)d.DayOfWeek + 7) % 7),
            _ => d.Day <= (monthDay ?? 1) ? new DateOnly(d.Year, d.Month, monthDay ?? 1) : new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays((monthDay ?? 1) - 1)
        };
    }
}

/// <summary>Schedules reports that modules offer (<see cref="IScheduledReport"/>) to be emailed daily, weekly or monthly.</summary>
internal sealed class ReportScheduleService(ErpDbContext db, ITenantContext tenant, IPermissionService permissions, IClock clock, IOutbox outbox,
    IEnumerable<IScheduledReport> reports)
{
    public const string MessageType = "core.scheduled_report";

    /// <summary>The reports the signed-in user may schedule.</summary>
    public async Task<List<ScheduledReportInfo>> AvailableAsync(CancellationToken ct)
    {
        var list = new List<ScheduledReportInfo>();
        foreach (var r in reports.OrderBy(r => r.Key))
            if (await permissions.HasAsync(r.Permission, ct)) list.Add(new ScheduledReportInfo(r.Key, r.NameEn, r.NameAr));
        return list;
    }

    public async Task<ReportSchedule> SaveAsync(Guid? id, ReportScheduleRequest r, CancellationToken ct)
    {
        var userId = tenant.UserId ?? throw new ForbiddenException();
        var report = reports.SingleOrDefault(x => x.Key == r.ReportKey)
                     ?? throw new BusinessRuleException("core.report_unknown", "Unknown report.", "تقرير غير معروف.");
        if (!await permissions.HasAsync(report.Permission, ct)) throw new ForbiddenException("You cannot view this report.");
        var recipients = CleanRecipients(r.Recipients);
        if (r.Frequency == ReportFrequency.Weekly && r.Weekday is not (>= 0 and <= 6))
            throw new BusinessRuleException("core.schedule_weekday", "Choose the day of the week.", "اختر يوم الأسبوع.");
        if (r.Frequency == ReportFrequency.Monthly && r.MonthDay is not (>= 1 and <= 28))
            throw new BusinessRuleException("core.schedule_monthday", "Choose a day of the month from 1 to 28.", "اختر يوماً من 1 إلى 28.");
        var schedule = id is null
            ? db.Add(new ReportSchedule { CompanyId = tenant.RequireCompanyId(), OwnerUserId = userId, ReportKey = r.ReportKey, Recipients = "" }).Entity
            : await FindAsync(id.Value, ct);
        if (id is not null && schedule.OwnerUserId != userId && !await permissions.HasAsync(CorePermissions.CompanyManage, ct)) throw new ForbiddenException();
        schedule.ReportKey = r.ReportKey;
        schedule.Frequency = r.Frequency;
        schedule.Weekday = r.Frequency == ReportFrequency.Weekly ? r.Weekday : null;
        schedule.MonthDay = r.Frequency == ReportFrequency.Monthly ? r.MonthDay : null;
        schedule.Recipients = string.Join(",", recipients);
        schedule.IsActive = r.IsActive;
        schedule.NextRunOn = ReportScheduleRules.NextRun(r.Frequency, schedule.Weekday, schedule.MonthDay, clock.Today);
        await db.SaveChangesAsync(ct);
        return schedule;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var schedule = await FindAsync(id, ct);
        if (schedule.OwnerUserId != tenant.UserId && !await permissions.HasAsync(CorePermissions.CompanyManage, ct)) throw new ForbiddenException();
        db.Remove(schedule);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues the email now (for trying a schedule out); the regular timetable is unchanged.</summary>
    public async Task SendNowAsync(Guid id, CancellationToken ct)
    {
        var schedule = await FindAsync(id, ct);
        outbox.Enqueue(MessageType, new { scheduleId = schedule.Id, companyId = schedule.CompanyId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues every due schedule of the current company and moves it to its next date (one transaction, so nothing is sent twice).</summary>
    public Task<int> QueueDueAsync(CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var today = clock.Today;
            var due = await db.Set<ReportSchedule>().Where(s => s.CompanyId == tenant.RequireCompanyId() && s.IsActive && s.NextRunOn <= today).ToListAsync(token);
            foreach (var s in due)
            {
                outbox.Enqueue(MessageType, new { scheduleId = s.Id, companyId = s.CompanyId });
                s.NextRunOn = ReportScheduleRules.NextRun(s.Frequency, s.Weekday, s.MonthDay, today);
            }
            await db.SaveChangesAsync(token);
            return due.Count;
        }, ct);

    public async Task<List<ReportScheduleDto>> ListAsync(CancellationToken ct)
    {
        var userId = tenant.UserId ?? throw new ForbiddenException();
        var all = await permissions.HasAsync(CorePermissions.CompanyManage, ct);
        var list = await db.Set<ReportSchedule>().AsNoTracking()
            .Where(s => s.CompanyId == tenant.RequireCompanyId() && (all || s.OwnerUserId == userId)).OrderBy(s => s.ReportKey).ToListAsync(ct);
        var owners = list.Select(s => s.OwnerUserId).Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayNameEn, ct);
        var byKey = reports.ToDictionary(r => r.Key);
        return list.Select(s => new ReportScheduleDto(s.Id, s.ReportKey, byKey.GetValueOrDefault(s.ReportKey)?.NameEn ?? s.ReportKey,
            byKey.GetValueOrDefault(s.ReportKey)?.NameAr ?? s.ReportKey, s.Frequency.ToString(), s.Weekday, s.MonthDay,
            s.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries), names.GetValueOrDefault(s.OwnerUserId, ""), s.IsActive, s.NextRunOn, s.LastSentAt,
            s.LastError)).ToList();
    }

    private async Task<ReportSchedule> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<ReportSchedule>().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Report schedule", id);

    private static List<string> CleanRecipients(IReadOnlyList<string> recipients)
    {
        var list = recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count is 0 or > 20)
            throw new BusinessRuleException("core.schedule_recipients", "Add between 1 and 20 email addresses.", "أضف من 1 إلى 20 عنوان بريد.");
        foreach (var r in list)
            if (!MailAddress.TryCreate(r, out var parsed) || parsed.Address != r)
                throw new BusinessRuleException("core.schedule_email", $"{r} is not a valid email address.", $"{r} ليس عنوان بريد صحيحاً.");
        return list;
    }
}

/// <summary>Renders the report as the schedule owner would see it and emails it; skipped (with a note) if the owner lost access.</summary>
internal sealed class ScheduledReportEmailHandler(ErpDbContext db, ITenantContext tenant, IPermissionService permissions, IEnumerable<IScheduledReport> reports,
    IEmailSender email, IClock clock) : IOutboxHandler
{
    public string MessageType => ReportScheduleService.MessageType;

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        tenant.Set(tenant.RequireTenantId(), payload.GetProperty("companyId").GetGuid(), null);
        var schedule = await db.Set<ReportSchedule>().SingleOrDefaultAsync(s => s.Id == payload.GetProperty("scheduleId").GetGuid(), ct);
        if (schedule is null) return;
        var report = reports.SingleOrDefault(r => r.Key == schedule.ReportKey);
        var owner = await db.Set<AppUser>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == schedule.OwnerUserId, ct);
        var granted = owner is { IsActive: true } ? await permissions.GetPermissionsAsync(owner.Id, ct) : new HashSet<string>();
        if (report is null || !(granted.Contains(DefaultRoles.Wildcard) || granted.Contains(report.Permission)))
        {
            schedule.LastError = "The owner can no longer view this report; nothing was sent.";
            await db.SaveChangesAsync(ct);
            return;
        }
        var file = await report.RenderAsync(ct);
        var company = await db.Set<Company>().AsNoTracking().Where(c => c.Id == schedule.CompanyId).Select(c => new { c.NameEn, c.NameAr }).SingleAsync(ct);
        string E(string s) => WebUtility.HtmlEncode(s);
        var html = $"""
            <div style="font-family:Tahoma,Arial,sans-serif;font-size:14px;color:#1d2524">
              <div dir="rtl" style="text-align:right"><h3 style="color:#0F5C4D;margin:0 0 8px">{E(report.NameAr)} — {E(company.NameAr)}</h3><p>{E(file.SummaryAr)}</p></div>
              <hr style="border:none;border-top:1px solid #d5dcda;margin:16px 0">
              <div dir="ltr"><h3 style="color:#0F5C4D;margin:0 0 8px">{E(report.NameEn)} — {E(company.NameEn)}</h3><p>{E(file.SummaryEn)}</p></div>
            </div>
            """;
        await email.SendAsync(new EmailMessage(schedule.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries),
            $"{report.NameAr} | {report.NameEn} — {company.NameEn}", html,
            $"{report.NameAr}\n{file.SummaryAr}\n\n{report.NameEn}\n{file.SummaryEn}",
            [new EmailAttachment(file.FileName, file.ContentType, file.Content)]), ct);
        schedule.LastSentAt = clock.UtcNow;
        schedule.LastError = null;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Every 30 minutes after 06:00 Qatar time: queues the schedules that are due today, once per day.</summary>
internal sealed class ReportScheduleJob(IServiceScopeFactory scopes, IClock clock, ILogger<ReportScheduleJob> logger) : BackgroundService
{
    private DateOnly _lastRun = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                if (clock.UtcNow.ToOffset(TimeSpan.FromHours(3)).Hour >= 6 && clock.Today > _lastRun)
                {
                    await RunOnceAsync(stoppingToken);
                    _lastRun = clock.Today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Report schedule job failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantCompany> companies;
        await using (var scope = scopes.CreateAsyncScope())
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().GetActiveCompaniesAsync(ct);
        var total = 0;
        foreach (var company in companies)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(company.TenantId, company.CompanyId, null);
            total += await scope.ServiceProvider.GetRequiredService<ReportScheduleService>().QueueDueAsync(ct);
        }
        return total;
    }
}

internal static class ReportScheduleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Any signed-in user may schedule the reports they can already see.
        var g = app.MapGroup("/api/core/report-schedules").WithTags("Platform").RequireAuthorization();
        g.MapGet("/reports", (ReportScheduleService s, CancellationToken ct) => s.AvailableAsync(ct));
        g.MapGet("", (ReportScheduleService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("", async (ReportScheduleRequest r, ReportScheduleService s, CancellationToken ct) => (await s.SaveAsync(null, r, ct)).Id);
        g.MapPut("/{id:guid}", async (Guid id, ReportScheduleRequest r, ReportScheduleService s, CancellationToken ct) => (await s.SaveAsync(id, r, ct)).Id);
        g.MapDelete("/{id:guid}", async (Guid id, ReportScheduleService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        g.MapPost("/{id:guid}/send-now", async (Guid id, ReportScheduleService s, CancellationToken ct) =>
        {
            await s.SendNowAsync(id, ct);
            return Results.Accepted();
        });
    }
}

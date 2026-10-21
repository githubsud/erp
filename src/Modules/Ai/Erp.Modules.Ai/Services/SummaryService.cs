using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Ai.Domain;
using Erp.Modules.Ai.Privacy;
using Erp.Modules.Ai.Providers;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Ai.Services;

public sealed record SummaryDto(DateOnly Date, string Language, string Text, string Facts, string Provider, DateTimeOffset CreatedAt, DateTimeOffset? EmailedAt,
    string WhatsAppUrl);

/// <summary>
/// The owner's daily summary: the day's figures from every module's dashboard, written up by the AI in a few lines (problems first).
/// If the AI is unavailable the figures themselves are the summary, so the owner always gets something.
/// </summary>
internal sealed class SummaryService(ErpDbContext db, ITenantContext tenant, AiGateway gateway, IEnumerable<IDashboardProvider> dashboards,
    IModuleAccess modules, IOutbox outbox, IClock clock, ILogger<SummaryService> logger)
{
    public const string EmailMessageType = "ai.summary_email";

    public async Task<SummaryDto?> LatestAsync(string? language, CancellationToken ct)
    {
        var q = db.Set<AiSummary>().AsNoTracking().Where(s => s.CompanyId == tenant.RequireCompanyId());
        if (language is not null) q = q.Where(s => s.Language == language);
        var s = await q.OrderByDescending(x => x.SummaryDate).ThenByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        return s is null ? null : ToDto(s);
    }

    /// <summary>Writes (or rewrites) today's summary in <paramref name="language"/>; with <paramref name="email"/> it is also queued for email.</summary>
    public async Task<SummaryDto> GenerateAsync(string language, bool email, CancellationToken ct)
    {
        language = language == "en" ? "en" : "ar";
        var facts = await FactsAsync(language, ct);
        string text, provider;
        try
        {
            var endpoint = await gateway.ResolveAsync(ct);
            var filter = new PrivacyFilter(!endpoint.IsPrivate);
            var system = $"""
                You write the evening summary for the owner of a small business in Qatar, in {(language == "ar" ? "Arabic" : "English")}.
                Use 4 to 7 short lines. Start with what needs attention (overdue money, cash differences, expiring documents, low stock), then the good news.
                Use the numbers exactly as given, in QAR. Do not invent anything that is not in the figures. Plain text, a dash at the start of each line, no tables.
                """;
            var response = await gateway.CompleteAsync(endpoint, new AiRequest(system, [AiMessage.User(filter.Text($"Figures for {clock.Today:yyyy-MM-dd}:\n{facts}"))],
                MaxTokens: 600), ct);
            text = filter.Restore(response.Text?.Trim() ?? "");
            provider = (response.AnsweredBy ?? endpoint).ProviderCode;
            if (text.Length == 0) (text, provider) = (facts, "none");
        }
        catch (BusinessRuleException ex)
        {
            logger.LogInformation("AI summary fell back to plain figures: {Message}", ex.Message);
            (text, provider) = (facts, "none");
        }
        var today = clock.Today;
        var summary = await db.Set<AiSummary>().SingleOrDefaultAsync(s => s.CompanyId == tenant.RequireCompanyId() && s.SummaryDate == today && s.Language == language, ct);
        if (summary is null)
            summary = db.Add(new AiSummary
            {
                CompanyId = tenant.RequireCompanyId(), SummaryDate = today, Language = language, Text = text, Facts = facts, Provider = provider
            }).Entity;
        else
        {
            summary.Text = text;
            summary.Facts = facts;
            summary.Provider = provider;
        }
        if (email)
        {
            outbox.Enqueue(EmailMessageType, new { summaryId = summary.Id, companyId = summary.CompanyId });
            summary.EmailQueuedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return ToDto(summary);
    }

    /// <summary>One line per dashboard figure of the modules that are on (the summary is for the owner, so no per-user filtering).</summary>
    public async Task<string> FactsAsync(string language, CancellationToken ct)
    {
        var off = await modules.DisabledAsync(ct);
        var lines = new StringBuilder();
        foreach (var p in dashboards)
        {
            if (p.Module is { } m && off.Contains(m)) continue;
            IReadOnlyList<DashboardTile> tiles;
            try
            {
                tiles = await p.GetTilesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dashboard provider {Provider} failed while building the AI summary", p.GetType().Name);
                continue;
            }
            foreach (var t in tiles)
            {
                var value = t.Format == "money" ? t.Value.ToString("#,##0.00", CultureInfo.InvariantCulture) + " QAR" : t.Value.ToString("0.##", CultureInfo.InvariantCulture);
                var label = language == "ar" ? t.LabelAr : t.LabelEn;
                var hint = language == "ar" ? t.HintAr : t.HintEn;
                lines.Append("- ").Append(label).Append(": ").Append(value);
                if (!string.IsNullOrEmpty(hint)) lines.Append(" (").Append(hint).Append(')');
                if (t.Tone is "bad" or "warn") lines.Append(" ⚠");
                lines.Append('\n');
            }
        }
        return lines.Length == 0 ? (language == "ar" ? "- لا توجد أرقام لهذا اليوم." : "- No figures for today.") : lines.ToString().TrimEnd();
    }

    public static SummaryDto ToDto(AiSummary s) =>
        new(s.SummaryDate, s.Language, s.Text, s.Facts, s.Provider, s.CreatedAt, s.EmailedAt, "https://wa.me/?text=" + Uri.EscapeDataString(s.Text));
}

/// <summary>Emails a summary to the company's chosen recipients, or to the users who may receive it.</summary>
internal sealed class SummaryEmailHandler(ErpDbContext db, ITenantContext tenant, IUserDirectory users, ICompanyProfileProvider company, IEmailSender email,
    IClock clock) : IOutboxHandler
{
    public string MessageType => SummaryService.EmailMessageType;

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        tenant.Set(tenant.RequireTenantId(), payload.GetProperty("companyId").GetGuid(), null);
        var summary = await db.Set<AiSummary>().SingleOrDefaultAsync(s => s.Id == payload.GetProperty("summaryId").GetGuid(), ct);
        if (summary is null) return;
        var setting = await db.Set<AiSetting>().AsNoTracking().SingleOrDefaultAsync(s => s.CompanyId == summary.CompanyId, ct);
        var to = setting?.SummaryRecipients is { Length: > 0 } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : await users.EmailsWithPermissionAsync(AiPermissions.SummaryView, ct);
        if (to.Count == 0) return;
        var profile = await company.GetAsync(ct);
        var ar = summary.Language == "ar";
        var title = ar ? $"ملخص اليوم — {profile.NameAr} — {summary.SummaryDate:dd/MM/yyyy}" : $"Today's summary — {profile.NameEn} — {summary.SummaryDate:dd/MM/yyyy}";
        var body = WebUtility.HtmlEncode(summary.Text).Replace("\n", "<br>");
        var html = $"""
            <div dir="{(ar ? "rtl" : "ltr")}" style="font-family:Tahoma,Arial,sans-serif;font-size:14px;color:#1d2524;text-align:{(ar ? "right" : "left")}">
              <h3 style="color:#0F5C4D;margin:0 0 8px">{WebUtility.HtmlEncode(title)}</h3><p>{body}</p>
            </div>
            """;
        await email.SendAsync(new EmailMessage(to.ToList(), title, html, $"{title}\n\n{summary.Text}"), ct);
        summary.EmailedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Every 15 minutes: companies whose summary hour has come (Qatar time) and that have not had today's summary get it written and emailed.</summary>
internal sealed class SummaryJob(IServiceScopeFactory scopes, IClock clock, ILogger<SummaryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI summary job failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantCompany> companies;
        await using (var scope = scopes.CreateAsyncScope())
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().GetActiveCompaniesAsync(ct);
        var hour = clock.UtcNow.ToOffset(TimeSpan.FromHours(3)).Hour;
        var sent = 0;
        foreach (var company in companies)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                sp.GetRequiredService<ITenantContext>().Set(company.TenantId, company.CompanyId, null);
                if ((await sp.GetRequiredService<IModuleAccess>().DisabledAsync(ct)).Contains(ModuleCodes.Ai)) continue;
                var db = sp.GetRequiredService<ErpDbContext>();
                var setting = await db.Set<AiSetting>().AsNoTracking().SingleOrDefaultAsync(s => s.CompanyId == company.CompanyId, ct);
                var (enabled, at, language) = setting is null ? (true, 21, "ar") : (setting.SummaryEnabled, setting.SummaryHour, setting.SummaryLanguage);
                if (!enabled || hour < at) continue;
                var today = clock.Today;
                if (await db.Set<AiSummary>().AnyAsync(s => s.CompanyId == company.CompanyId && s.SummaryDate == today && s.EmailQueuedAt != null, ct)) continue;
                await sp.GetRequiredService<SummaryService>().GenerateAsync(language, true, ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "AI summary failed for tenant {Tenant}", company.TenantId);
            }
        }
        return sent;
    }
}

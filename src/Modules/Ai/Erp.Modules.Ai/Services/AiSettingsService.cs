using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Ai.Domain;
using Erp.Modules.Ai.Providers;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Erp.Modules.Ai.Services;

public sealed record PlatformProviderInfo(string Code, string Name, bool IsPrivate, bool IsDefault, string Model, bool CanReadImages, int DailyLimit);

public sealed record EffectiveAi(string Provider, string Name, bool IsPrivate, string Model, bool CanReadImages, int UsedToday, int? DailyLimit);

public sealed record AiSettingsDto(string Mode, string? ProviderCode, string? Kind, string? BaseUrl, string? Model, string? VisionModel, bool HasApiKey,
    bool SummaryEnabled, int SummaryHour, string SummaryLanguage, IReadOnlyList<string> SummaryRecipients, IReadOnlyList<PlatformProviderInfo> PlatformProviders,
    EffectiveAi? Effective);

/// <summary><see cref="ApiKey"/>: null keeps the stored key, empty removes it.</summary>
public sealed record AiSettingsRequest(AiMode Mode, string? ProviderCode, AiProviderKind? Kind, string? BaseUrl, string? Model, string? VisionModel, string? ApiKey,
    bool SummaryEnabled = true, int SummaryHour = 21, string SummaryLanguage = "ar", IReadOnlyList<string>? SummaryRecipients = null);

public sealed record AiTestResult(bool Ok, string? Reply, long Milliseconds, string? Error);

public sealed record UsageDay(DateOnly Day, string Provider, int Requests, long InputTokens, long OutputTokens);

/// <summary>A company's AI choice: a platform provider, or its own subscription with any OpenAI-compatible service or Claude.</summary>
internal sealed class AiSettingsService(ErpDbContext db, ITenantContext tenant, AiGateway gateway, IConfiguration configuration, IClock clock)
{
    public async Task<AiSettingsDto> GetAsync(CancellationToken ct)
    {
        var s = await gateway.SettingAsync(ct);
        var providers = await db.Set<AiProvider>().AsNoTracking().Where(p => p.IsEnabled).OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
            .Select(p => new PlatformProviderInfo(p.Code, p.Name, p.IsPrivate, p.IsDefault, p.Model, p.VisionModel != null, p.DailyLimit)).ToListAsync(ct);
        return new AiSettingsDto((s?.Mode ?? AiMode.Platform).ToString(), s?.ProviderCode, s?.Kind?.ToString(), s?.BaseUrl, s?.Model, s?.VisionModel,
            !string.IsNullOrEmpty(s?.ApiKey), s?.SummaryEnabled ?? true, s?.SummaryHour ?? 21, s?.SummaryLanguage ?? "ar",
            (s?.SummaryRecipients ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), providers, await EffectiveAsync(ct));
    }

    /// <summary>The provider in use now and today's usage; null when none is available.</summary>
    public async Task<EffectiveAi?> EffectiveAsync(CancellationToken ct)
    {
        try
        {
            var e = await gateway.ResolveAsync(ct);
            return new EffectiveAi(e.ProviderCode, e.Name, e.IsPrivate, e.Model, !string.IsNullOrWhiteSpace(e.VisionModel), await gateway.UsedTodayAsync(e.ProviderCode, ct),
                e.DailyLimit);
        }
        catch (BusinessRuleException)
        {
            return null;
        }
    }

    public async Task<AiSettingsDto> SaveAsync(AiSettingsRequest r, CancellationToken ct)
    {
        var s = await gateway.SettingAsync(ct) ?? db.Add(new AiSetting { CompanyId = tenant.RequireCompanyId() }).Entity;
        Apply(s, r, await db.Set<AiProvider>().AsNoTracking().Where(p => p.IsEnabled).Select(p => p.Code).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    /// <summary>Sends a one-word request with the given settings (not saved), or with the saved ones when <paramref name="r"/> is null.</summary>
    public async Task<AiTestResult> TestAsync(AiSettingsRequest? r, CancellationToken ct)
    {
        AiEndpoint endpoint;
        if (r is null) endpoint = await gateway.ResolveAsync(ct);
        else
        {
            var stored = await gateway.SettingAsync(ct);
            var probe = new AiSetting { CompanyId = tenant.RequireCompanyId(), ApiKey = stored?.ApiKey };
            Apply(probe, r, await db.Set<AiProvider>().AsNoTracking().Where(p => p.IsEnabled).Select(p => p.Code).ToListAsync(ct));
            if (probe.Mode == AiMode.Own)
                endpoint = new AiEndpoint(AiGateway.OwnCode, "Own subscription", probe.Kind!.Value, probe.BaseUrl!, probe.Model!, probe.VisionModel, probe.ApiKey, true, null);
            else
            {
                var provider = await db.Set<AiProvider>().AsNoTracking().Where(p => p.IsEnabled && (p.Code == probe.ProviderCode || (probe.ProviderCode == null && p.IsDefault)))
                    .FirstOrDefaultAsync(ct) ?? throw new BusinessRuleException("ai.not_configured", "No AI provider is available.", "لا يوجد مزوّد ذكاء اصطناعي متاح.");
                endpoint = AiGateway.ToEndpoint(provider);
            }
        }
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await gateway.CompleteAsync(endpoint, new AiRequest("Reply with the single word OK.", [AiMessage.User("Test")], MaxTokens: 200), ct);
            return string.IsNullOrWhiteSpace(response.Text)
                ? new AiTestResult(false, null, watch.ElapsedMilliseconds, "The model answered with nothing; try another model name.")
                : new AiTestResult(true, response.Text.Trim(), watch.ElapsedMilliseconds, null);
        }
        catch (BusinessRuleException ex)
        {
            return new AiTestResult(false, null, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<List<UsageDay>> UsageAsync(int days, CancellationToken ct)
    {
        var since = clock.Today.AddDays(-Math.Clamp(days, 1, 365));
        return await db.Database.SqlQuery<UsageDay>($"""
            SELECT day, provider, requests, input_tokens, output_tokens FROM ai.usage_day
             WHERE tenant_id = {tenant.RequireTenantId()} AND day >= {since} ORDER BY day DESC, provider
            """).ToListAsync(ct);
    }

    private void Apply(AiSetting s, AiSettingsRequest r, IReadOnlyCollection<string> platformCodes)
    {
        if (r.SummaryHour is < 0 or > 23)
            throw new BusinessRuleException("ai.summary_hour", "Choose an hour from 0 to 23.", "اختر ساعة من 0 إلى 23.");
        var recipients = (r.SummaryRecipients ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Count > 10)
            throw new BusinessRuleException("ai.summary_recipients", "Add up to 10 email addresses.", "أضف 10 عناوين بريد كحد أقصى.");
        foreach (var e in recipients)
            if (!MailAddress.TryCreate(e, out var parsed) || parsed.Address != e)
                throw new BusinessRuleException("ai.summary_email", $"{e} is not a valid email address.", $"{e} ليس عنوان بريد صحيحاً.");
        s.SummaryEnabled = r.SummaryEnabled;
        s.SummaryHour = r.SummaryHour;
        s.SummaryLanguage = r.SummaryLanguage == "en" ? "en" : "ar";
        s.SummaryRecipients = recipients.Count == 0 ? null : string.Join(",", recipients);
        s.Mode = r.Mode;
        if (r.ApiKey is not null) s.ApiKey = r.ApiKey.Trim().Length == 0 ? null : r.ApiKey.Trim();
        if (r.Mode == AiMode.Platform)
        {
            if (r.ProviderCode is not null && !platformCodes.Contains(r.ProviderCode))
                throw new BusinessRuleException("ai.provider_unknown", "That AI provider is not available.", "مزوّد الذكاء الاصطناعي هذا غير متاح.");
            s.ProviderCode = r.ProviderCode;
            return;
        }
        if (r.Kind is null || string.IsNullOrWhiteSpace(r.BaseUrl) || string.IsNullOrWhiteSpace(r.Model))
            throw new BusinessRuleException("ai.own_incomplete", "Choose the provider and enter the model name.", "اختر المزوّد وأدخل اسم النموذج.");
        CheckUrl(r.BaseUrl.Trim());
        if (r.Kind == AiProviderKind.Anthropic && string.IsNullOrEmpty(s.ApiKey))
            throw new BusinessRuleException("ai.key_required", "Enter your API key.", "أدخل مفتاح الواجهة البرمجية (API key).");
        s.Kind = r.Kind;
        s.BaseUrl = r.BaseUrl.Trim();
        s.Model = r.Model.Trim();
        s.VisionModel = string.IsNullOrWhiteSpace(r.VisionModel) ? null : r.VisionModel.Trim();
    }

    /// <summary>A company's own AI address must be public HTTPS, so the server cannot be pointed at internal services.</summary>
    private void CheckUrl(string url)
    {
        if (configuration.GetValue("Ai:AllowPrivateUrls", false)) return;
        var invalid = new BusinessRuleException("ai.url_invalid", "The AI address must be a public https:// address.", "يجب أن يكون عنوان الذكاء الاصطناعي عنوان https:// عاماً.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback) throw invalid;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var ip) ? [ip] : Dns.GetHostAddresses(uri.Host);
        }
        catch (SocketException)
        {
            throw new BusinessRuleException("ai.url_unknown", "The AI address could not be found.", "تعذر العثور على عنوان الذكاء الاصطناعي.");
        }
        if (addresses.Length == 0 || addresses.Any(IsInternal)) throw invalid;
    }

    internal static bool IsInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] is 10 or 127 or 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}

public sealed record ProviderAdminDto(string Code, string Name, string Kind, string BaseUrl, string Model, string? VisionModel, bool HasApiKey, bool IsPrivate,
    bool IsDefault, bool IsEnabled, int DailyLimit, string? FallbackModels, string? FallbackProvider);

/// <summary><see cref="ApiKey"/>: null keeps the stored key, empty removes it.</summary>
public sealed record ProviderRequest(string? Code, string Name, AiProviderKind Kind, string BaseUrl, string Model, string? VisionModel, string? ApiKey,
    bool IsPrivate, bool IsDefault, bool IsEnabled, int DailyLimit, string? FallbackModels = null, string? FallbackProvider = null);

public sealed record CustomerUsage(Guid TenantId, string Provider, int Requests, long InputTokens, long OutputTokens, int RequestsToday);

/// <summary>The platform's AI providers (free tier, our own server…), which one customers get by default, and usage per customer.</summary>
internal sealed class AiBackOfficeService(ErpDbContext db, IAiChatClient client, IClock clock)
{
    public async Task<List<ProviderAdminDto>> ProvidersAsync(CancellationToken ct) =>
        (await db.Set<AiProvider>().AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct))
        .Select(p => new ProviderAdminDto(p.Code, p.Name, p.Kind.ToString(), p.BaseUrl, p.Model, p.VisionModel, !string.IsNullOrEmpty(p.ApiKey), p.IsPrivate,
            p.IsDefault, p.IsEnabled, p.DailyLimit, p.FallbackModels, p.FallbackProvider)).ToList();

    public Task SaveAsync(string? code, ProviderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Model) || !Uri.TryCreate(r.BaseUrl, UriKind.Absolute, out _))
                throw new BusinessRuleException("ai.provider_invalid", "Enter the name, address and model.", "أدخل الاسم والعنوان والنموذج.");
            if (r.DailyLimit < 0) throw new BusinessRuleException("ai.provider_limit", "The daily limit cannot be negative.", "لا يمكن أن يكون الحد اليومي سالباً.");
            AiProvider p;
            if (code is null)
            {
                var newCode = (r.Code ?? "").Trim().ToLowerInvariant();
                if (newCode.Length is < 2 or > 30 || newCode == AiGateway.OwnCode || !newCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    throw new BusinessRuleException("ai.provider_code", "Use a short code of letters and digits.", "استخدم رمزاً قصيراً من حروف وأرقام.");
                if (await db.Set<AiProvider>().AnyAsync(x => x.Code == newCode, token))
                    throw new BusinessRuleException("ai.provider_exists", "A provider with this code exists.", "يوجد مزوّد بهذا الرمز.");
                p = db.Add(new AiProvider { Code = newCode, Name = r.Name, BaseUrl = r.BaseUrl, Model = r.Model, CreatedAt = clock.UtcNow }).Entity;
            }
            else p = await db.Set<AiProvider>().SingleOrDefaultAsync(x => x.Code == code, token) ?? throw new NotFoundException("AI provider", code);
            if (r.IsDefault && !p.IsDefault)
            {
                // Only one default: clear the old one first (the unique index is checked row by row).
                await db.Set<AiProvider>().Where(x => x.IsDefault).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsDefault, false), token);
            }
            p.Name = r.Name.Trim();
            p.Kind = r.Kind;
            p.BaseUrl = r.BaseUrl.Trim();
            p.Model = r.Model.Trim();
            p.VisionModel = string.IsNullOrWhiteSpace(r.VisionModel) ? null : r.VisionModel.Trim();
            var fallbacks = (r.FallbackModels ?? "").Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            p.FallbackModels = fallbacks.Length == 0 ? null : string.Join(",", fallbacks.Distinct());
            var fallbackProvider = string.IsNullOrWhiteSpace(r.FallbackProvider) ? null : r.FallbackProvider.Trim();
            if (fallbackProvider is not null && (fallbackProvider == p.Code || !await db.Set<AiProvider>().AnyAsync(x => x.Code == fallbackProvider, token)))
                throw new BusinessRuleException("ai.fallback_invalid", "Choose another existing provider as the fallback.", "اختر مزوّداً آخر موجوداً كبديل.");
            p.FallbackProvider = fallbackProvider;
            if (r.ApiKey is not null) p.ApiKey = r.ApiKey.Trim().Length == 0 ? null : r.ApiKey.Trim();
            p.IsPrivate = r.IsPrivate;
            p.IsDefault = r.IsDefault && r.IsEnabled;
            p.IsEnabled = r.IsEnabled;
            p.DailyLimit = r.DailyLimit;
            p.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(token);
            return true;
        }, ct);

    public async Task<AiTestResult> TestAsync(string code, CancellationToken ct)
    {
        var p = await db.Set<AiProvider>().AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, ct) ?? throw new NotFoundException("AI provider", code);
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await client.CompleteAsync(AiGateway.ToEndpoint(p), new AiRequest("Reply with the single word OK.", [AiMessage.User("Test")], MaxTokens: 200), ct);
            return string.IsNullOrWhiteSpace(response.Text)
                ? new AiTestResult(false, null, watch.ElapsedMilliseconds, "The model answered with nothing; try another model name.")
                : new AiTestResult(true, response.Text.Trim(), watch.ElapsedMilliseconds, null);
        }
        catch (AiProviderException ex)
        {
            return new AiTestResult(false, null, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<List<CustomerUsage>> UsageAsync(int days, CancellationToken ct)
    {
        var today = clock.Today;
        var since = today.AddDays(-Math.Clamp(days, 1, 365));
        return await db.Database.SqlQuery<CustomerUsage>($"""
            SELECT tenant_id, provider, sum(requests)::int AS requests, sum(input_tokens)::bigint AS input_tokens, sum(output_tokens)::bigint AS output_tokens,
                   coalesce(sum(requests) FILTER (WHERE day = {today}), 0)::int AS requests_today
              FROM ai.usage_day WHERE day >= {since}
             GROUP BY tenant_id, provider ORDER BY sum(requests) DESC
            """).ToListAsync(ct);
    }
}

using Erp.Infrastructure.Persistence;
using Erp.Modules.Ai.Domain;
using Erp.Modules.Ai.Providers;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Ai.Services;

/// <summary>
/// Every AI call of a company goes through here: picks the company's provider (its own subscription, or the platform's chosen or default
/// provider), enforces the platform's daily allowance and records usage.
/// </summary>
internal sealed class AiGateway(ErpDbContext db, ITenantContext tenant, IAiChatClient client, IClock clock, ILogger<AiGateway> logger)
{
    public const string OwnCode = "own";

    public async Task<AiSetting?> SettingAsync(CancellationToken ct) =>
        await db.Set<AiSetting>().SingleOrDefaultAsync(s => s.CompanyId == tenant.RequireCompanyId(), ct);

    public async Task<AiEndpoint> ResolveAsync(CancellationToken ct)
    {
        var setting = await SettingAsync(ct);
        if (setting is { Mode: AiMode.Own })
            return new AiEndpoint(OwnCode, "Own subscription", setting.Kind ?? AiProviderKind.OpenAiCompatible, setting.BaseUrl!, setting.Model!,
                setting.VisionModel ?? setting.Model, setting.ApiKey, true, null);
        var providers = await db.Set<AiProvider>().AsNoTracking().Where(p => p.IsEnabled).ToListAsync(ct);
        var provider = providers.FirstOrDefault(p => p.Code == setting?.ProviderCode) ?? providers.FirstOrDefault(p => p.IsDefault)
                       ?? throw new BusinessRuleException("ai.not_configured", "No AI provider is available. Choose one in AI settings.",
                           "لا يوجد مزوّد ذكاء اصطناعي متاح. اختر واحداً من إعدادات الذكاء الاصطناعي.");
        return ToEndpoint(provider);
    }

    public static AiEndpoint ToEndpoint(AiProvider p) =>
        new(p.Code, p.Name, p.Kind, p.BaseUrl, p.Model, p.VisionModel, p.ApiKey, p.IsPrivate, p.DailyLimit);

    public async Task<AiResponse> CompleteAsync(AiEndpoint endpoint, AiRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        if (endpoint.DailyLimit is { } limit && await UsedTodayAsync(endpoint.ProviderCode, ct) >= limit)
            throw new BusinessRuleException("ai.daily_limit", $"Today's free AI allowance ({limit} requests) is used up. It renews tomorrow, or connect your own AI subscription.",
                $"تم استهلاك الحد اليومي المجاني ({limit} طلب). يتجدد غداً، أو اربط اشتراكك الخاص في الذكاء الاصطناعي.");
        AiResponse response;
        try
        {
            response = await client.CompleteAsync(endpoint, request, ct);
        }
        catch (AiProviderException ex)
        {
            logger.LogWarning("AI provider {Provider} failed: {Message}", endpoint.ProviderCode, ex.Message);
            await RecordAsync(tenantId, endpoint.ProviderCode, 0, 0, ct);
            throw new BusinessRuleException("ai.provider_error", $"The AI service did not answer: {ex.Message}", $"لم تستجب خدمة الذكاء الاصطناعي: {ex.Message}");
        }
        await RecordAsync(tenantId, endpoint.ProviderCode, response.InputTokens, response.OutputTokens, ct);
        return response;
    }

    public async Task<int> UsedTodayAsync(string provider, CancellationToken ct) =>
        await db.Database.SqlQuery<int>($"""
            SELECT requests AS "Value" FROM ai.usage_day WHERE tenant_id = {tenant.RequireTenantId()} AND day = {clock.Today} AND provider = {provider}
            """).SingleOrDefaultAsync(ct);

    private Task RecordAsync(Guid tenantId, string provider, int input, int output, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai.usage_day (tenant_id, day, provider, requests, input_tokens, output_tokens)
            VALUES ({tenantId}, {clock.Today}, {provider}, 1, {input}, {output})
            ON CONFLICT (tenant_id, day, provider) DO UPDATE SET requests = ai.usage_day.requests + 1,
                input_tokens = ai.usage_day.input_tokens + excluded.input_tokens, output_tokens = ai.usage_day.output_tokens + excluded.output_tokens
            """, ct);
}

using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Web;
using Erp.Modules.Ai.Providers;
using Erp.Modules.Ai.Services;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Ai;

public static class AiPermissions
{
    public const string AssistantUse = "ai.assistant:use";
    public const string SummaryView = "ai.summary:view";
    public const string SettingsManage = "ai.settings:manage";
    public const string DocumentsRead = "ai.documents:read";
}

internal sealed class AiPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(AiPermissions.AssistantUse, "Ask the AI assistant and draft messages", "سؤال المساعد الذكي وصياغة الرسائل", GeneralManager, Accountant,
            HrManager, PayrollOfficer, Purchaser, Storekeeper, Salesperson),
        new(AiPermissions.SummaryView, "Receive the daily AI summary", "استلام الملخص اليومي الذكي", GeneralManager),
        new(AiPermissions.SettingsManage, "Choose the AI provider", "اختيار مزوّد الذكاء الاصطناعي", GeneralManager),
        new(AiPermissions.DocumentsRead, "Read ID and passport photos with AI", "قراءة صور الهوية والجواز بالذكاء الاصطناعي", GeneralManager, HrManager)
    ];
}

public sealed class AiModule : IModule
{
    public string Name => "Ai";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Small models on a CPU can take a minute or two.
        services.AddHttpClient(HttpAiChatClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Ai:TimeoutSeconds", 180)));
        services.AddSingleton<IAiChatClient, HttpAiChatClient>();
        services.AddScoped<AiGateway>();
        services.AddScoped<AssistantService>();
        services.AddScoped<SummaryService>();
        services.AddScoped<DraftService>();
        services.AddScoped<DocumentReader>();
        services.AddScoped<AiSettingsService>();
        services.AddScoped<AiBackOfficeService>();
        services.AddScoped<IAiTool, DashboardAiTool>();
        services.AddScoped<IOutboxHandler, SummaryEmailHandler>();
        services.AddSingleton<SummaryJob>();
        if (configuration.GetValue("Jobs:AiSummary:Enabled", true))
            services.AddHostedService(sp => sp.GetRequiredService<SummaryJob>());
        services.AddSingleton<IPermissionProvider, AiPermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/ai").WithTags("AI").RequireAuthorization();
        g.MapGet("/status", (AiSettingsService s, CancellationToken ct) => s.EffectiveAsync(ct)).RequirePermission(AiPermissions.AssistantUse);
        g.MapGet("/settings", (AiSettingsService s, CancellationToken ct) => s.GetAsync(ct)).RequirePermission(AiPermissions.SettingsManage);
        g.MapPut("/settings", (AiSettingsRequest r, AiSettingsService s, CancellationToken ct) => s.SaveAsync(r, ct)).RequirePermission(AiPermissions.SettingsManage);
        g.MapPost("/settings/test", (AiSettingsRequest? r, AiSettingsService s, CancellationToken ct) => s.TestAsync(r, ct))
            .RequirePermission(AiPermissions.SettingsManage);
        g.MapGet("/usage", (AiSettingsService s, CancellationToken ct, int days = 30) => s.UsageAsync(days, ct)).RequirePermission(AiPermissions.SettingsManage);
        g.MapGet("/tools", async (AssistantService s, CancellationToken ct) => (await s.AvailableToolsAsync(ct)).Select(t => new { t.Name, t.Description }))
            .RequirePermission(AiPermissions.AssistantUse);
        g.MapPost("/ask", (AskRequest r, AssistantService s, CancellationToken ct) => s.AskAsync(r, ct)).RequirePermission(AiPermissions.AssistantUse);
        g.MapPost("/draft", (DraftRequest r, DraftService s, CancellationToken ct) => s.DraftAsync(r, ct)).RequirePermission(AiPermissions.AssistantUse);
        g.MapGet("/summary/latest", async (SummaryService s, CancellationToken ct, string? language = null) =>
            await s.LatestAsync(language, ct) is { } dto ? Results.Ok(dto) : Results.NoContent()).RequirePermission(AiPermissions.SummaryView);
        g.MapPost("/summary", (SummaryRequest r, SummaryService s, CancellationToken ct) => s.GenerateAsync(r.Language ?? "ar", r.Email, ct))
            .RequirePermission(AiPermissions.SummaryView);
        g.MapPost("/read-document", async (IFormFile file, string kind, DocumentReader reader, CancellationToken ct) =>
        {
            // Too large: an empty photo gets the same "up to 5 MB" refusal without reading it.
            if (file.Length > DocumentReader.MaxBytes) return Results.Ok(await reader.ReadAsync(kind, file.ContentType, [], ct));
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            return Results.Ok(await reader.ReadAsync(kind, file.ContentType, buffer.ToArray(), ct));
        }).RequirePermission(AiPermissions.DocumentsRead).DisableAntiforgery();

        var bo = app.MapGroup("/api/backoffice/ai").WithTags("Back-office").RequirePlatformAdmin();
        bo.MapGet("/providers", (AiBackOfficeService s, CancellationToken ct) => s.ProvidersAsync(ct));
        bo.MapPost("/providers", async (ProviderRequest r, AiBackOfficeService s, CancellationToken ct) =>
        {
            await s.SaveAsync(null, r, ct);
            return await s.ProvidersAsync(ct);
        });
        bo.MapPut("/providers/{code}", async (string code, ProviderRequest r, AiBackOfficeService s, CancellationToken ct) =>
        {
            await s.SaveAsync(code, r, ct);
            return await s.ProvidersAsync(ct);
        });
        bo.MapPost("/providers/{code}/test", (string code, AiBackOfficeService s, CancellationToken ct) => s.TestAsync(code, ct));
        bo.MapGet("/usage", (AiBackOfficeService s, CancellationToken ct, int days = 30) => s.UsageAsync(days, ct));
    }
}

public sealed record SummaryRequest(string? Language, bool Email = false);

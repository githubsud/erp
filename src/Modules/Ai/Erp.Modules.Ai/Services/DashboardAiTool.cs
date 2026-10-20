using System.Text.Json;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Ai.Services;

/// <summary>The figures on the user's home dashboard, so general "how is business today" questions have an answer.</summary>
internal sealed class DashboardAiTool(IEnumerable<IDashboardProvider> providers, IPermissionService permissions, IModuleAccess modules) : IAiTool
{
    public const string ToolName = "key_figures";
    public string Name => ToolName;
    public string Description =>
        "Today's and this month's key figures from every area the user can see: sales today, refunds, cash in tills, cash and bank, revenue and profit this month, receivables, overdue, payables, stock value, orders waiting. Use for general questions about how the business is doing.";
    public string Permission => AiPermissions.AssistantUse;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var off = await modules.DisabledAsync(ct);
        var figures = new List<object>();
        foreach (var p in providers)
        {
            if ((p.Module is { } m && off.Contains(m)) || !await permissions.HasAsync(p.Permission, ct)) continue;
            foreach (var t in await p.GetTilesAsync(ct))
                figures.Add(new { area = t.Section, figure = t.LabelEn, figure_ar = t.LabelAr, value = t.Value, unit = t.Format == "money" ? "QAR" : "count", note = t.HintEn,
                    attention = t.Tone is "bad" or "warn" });
        }
        return new { figures };
    }
}

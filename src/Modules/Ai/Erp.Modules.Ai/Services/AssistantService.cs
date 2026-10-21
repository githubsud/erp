using System.Text.Json;
using Erp.Modules.Ai.Privacy;
using Erp.Modules.Ai.Providers;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Ai.Services;

public sealed record ChatTurn(string Role, string Text);

public sealed record AskRequest(string Question, IReadOnlyList<ChatTurn>? History = null);

public sealed record AskResponse(string Answer, IReadOnlyList<string> ToolsUsed, string Provider, bool Private);

/// <summary>
/// "Ask your business": the model answers from the ERP's own reports, which it reaches only through read-only tools the user may use.
/// It never queries the database itself and never changes anything.
/// </summary>
internal sealed class AssistantService(AiGateway gateway, IEnumerable<IAiTool> tools, IPermissionService permissions, IModuleAccess modules,
    ICompanyProfileProvider company, IClock clock, ILogger<AssistantService> logger)
{
    public const int MaxRounds = 5;

    public async Task<IReadOnlyList<IAiTool>> AvailableToolsAsync(CancellationToken ct)
    {
        var off = await modules.DisabledAsync(ct);
        var list = new List<IAiTool>();
        foreach (var t in tools.OrderBy(t => t.Name))
            if ((t.Module is null || !off.Contains(t.Module)) && await permissions.HasAsync(t.Permission, ct)) list.Add(t);
        return list;
    }

    public async Task<AskResponse> AskAsync(AskRequest r, CancellationToken ct)
    {
        var question = r.Question?.Trim() ?? "";
        if (question.Length is 0 or > 1000)
            throw new BusinessRuleException("ai.question_length", "Write a question of up to 1000 characters.", "اكتب سؤالاً لا يزيد عن 1000 حرف.");
        var endpoint = await gateway.ResolveAsync(ct);
        var isPrivate = endpoint.IsPrivate;
        var filter = new PrivacyFilter(!isPrivate);
        var available = await AvailableToolsAsync(ct);
        var byName = available.ToDictionary(t => t.Name);
        var specs = available.Select(t => new AiToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();
        var profile = await company.GetAsync(ct);

        var messages = new List<AiMessage>();
        foreach (var turn in (r.History ?? []).TakeLast(6))
            messages.Add(turn.Role == "assistant" ? AiMessage.Assistant(filter.Text(turn.Text)) : AiMessage.User(filter.Text(turn.Text)));
        messages.Add(AiMessage.User(filter.Text(question)));
        var system = $"""
            You are the assistant inside the ERP of {filter.Personal(profile.NameEn)}, a small business in Qatar. Today is {clock.Today:yyyy-MM-dd} ({clock.Today.DayOfWeek}).
            Amounts are in Qatari riyals (QAR). Answer only from the tool results; never invent figures. If no tool can answer, say briefly what you can answer instead.
            Use a tool whenever the question is about the business. Dates in tool arguments are yyyy-MM-dd.
            Reply in the language of the question (Arabic or English). Be short: one or two sentences, then a few bullet points if useful. No tables.
            {filter.PlaceholderRule}
            {await KeyFiguresAsync(byName, filter, ct)}
            """;

        var used = new List<string>();
        for (var round = 0; round < MaxRounds; round++)
        {
            var response = await gateway.CompleteAsync(endpoint, new AiRequest(system, messages, specs.Count > 0 ? specs : null), ct);
            // Once a fallback answered, the next rounds go straight to it instead of waiting on the used-up provider again.
            endpoint = response.AnsweredBy ?? endpoint;
            if (response.ToolCalls.Count == 0)
                return new AskResponse(filter.Restore(response.Text?.Trim() ?? ""), used.Distinct().ToList(), endpoint.Name, isPrivate);
            messages.Add(AiMessage.Assistant(response.Text, response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                used.Add(call.Name);
                messages.Add(AiMessage.ToolResult(call.Id, await RunToolAsync(byName, call, filter, ct)));
            }
        }
        // Still asking for data after several rounds: make it answer with what it has.
        var last = await gateway.CompleteAsync(endpoint, new AiRequest(system, messages), ct);
        return new AskResponse(filter.Restore(last.Text?.Trim() ?? ""), used.Distinct().ToList(), (last.AnsweredBy ?? endpoint).Name, isPrivate);
    }

    /// <summary>Today's dashboard figures up front, so general questions are answered even by models that are weak at calling tools.</summary>
    private static async Task<string> KeyFiguresAsync(IReadOnlyDictionary<string, IAiTool> byName, PrivacyFilter filter, CancellationToken ct)
    {
        if (!byName.TryGetValue(DashboardAiTool.ToolName, out var tool)) return "";
        try
        {
            return "Key figures right now (use a tool for anything else): " + filter.Json(await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, ct));
        }
        catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException)
        {
            return "";
        }
    }

    private async Task<string> RunToolAsync(IReadOnlyDictionary<string, IAiTool> byName, AiToolCall call, PrivacyFilter filter, CancellationToken ct)
    {
        if (!byName.TryGetValue(call.Name, out var tool)) return filter.Json(new { error = $"There is no tool named {call.Name}." });
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : filter.Restore(call.Arguments)).RootElement;
        }
        catch (JsonException)
        {
            args = JsonDocument.Parse("{}").RootElement;
        }
        try
        {
            return filter.Json(await tool.InvokeAsync(args, ct));
        }
        catch (Exception ex) when (ex is BusinessRuleException or NotFoundException or ArgumentException or FormatException)
        {
            logger.LogInformation("AI tool {Tool} refused: {Message}", call.Name, ex.Message);
            return filter.Json(new { error = ex.Message });
        }
    }
}

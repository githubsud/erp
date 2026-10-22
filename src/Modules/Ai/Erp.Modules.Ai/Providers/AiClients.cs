using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Modules.Ai.Domain;

namespace Erp.Modules.Ai.Providers;

/// <summary>Where a request goes. <see cref="ProviderCode"/> is the platform provider's code, or "own" for the company's own subscription.</summary>
public sealed record AiEndpoint(string ProviderCode, string Name, AiProviderKind Kind, string BaseUrl, string Model, string? VisionModel, string? ApiKey,
    bool IsPrivate, int? DailyLimit, IReadOnlyList<string>? FallbackModels = null, string? FallbackProvider = null);

/// <summary><see cref="ProviderData"/>: what the provider attached to the call and wants back with it (Gemini's thought signature), as JSON.</summary>
public sealed record AiToolCall(string Id, string Name, string Arguments, string? ProviderData = null);

public sealed record AiImage(string MediaType, byte[] Data);

public sealed record AiMessage(string Role, string? Text, IReadOnlyList<AiToolCall>? ToolCalls = null, string? ToolCallId = null, AiImage? Image = null)
{
    public static AiMessage User(string text, AiImage? image = null) => new("user", text, Image: image);
    public static AiMessage Assistant(string? text, IReadOnlyList<AiToolCall>? calls = null) => new("assistant", text, calls);
    public static AiMessage ToolResult(string callId, string content) => new("tool", content, ToolCallId: callId);
}

public sealed record AiToolSpec(string Name, string Description, string ParametersSchema);

/// <summary>
/// <see cref="MaxTokens"/> must leave room for the model's own thinking: newer models (Gemini 3.5, gpt-oss) count it against the same limit,
/// and a small limit cuts the answer off half-way through.
/// </summary>
public sealed record AiRequest(string System, IReadOnlyList<AiMessage> Messages, IReadOnlyList<AiToolSpec>? Tools = null, bool Vision = false,
    int MaxTokens = 4000, bool Json = false);

/// <summary>
/// <see cref="AnsweredBy"/>: the provider that answered, set by the gateway (a fallback when the chosen one was used up).
/// <see cref="Truncated"/>: the answer stopped at the token limit, so its text is incomplete.
/// </summary>
public sealed record AiResponse(string? Text, IReadOnlyList<AiToolCall> ToolCalls, int InputTokens, int OutputTokens, AiEndpoint? AnsweredBy = null,
    bool Truncated = false);

/// <summary><see cref="Busy"/>: the provider is overloaded or its free allowance per minute is used up (try again shortly).</summary>
public sealed class AiProviderException(string message, bool busy = false) : Exception(message)
{
    public bool Busy { get; } = busy;
}

/// <summary>Sends a chat to a model. One implementation talks to real providers; tests replace it.</summary>
public interface IAiChatClient
{
    Task<AiResponse> CompleteAsync(AiEndpoint endpoint, AiRequest request, CancellationToken ct);
}

/// <summary>
/// Two wire formats cover the providers: the OpenAI chat-completions format (OpenAI, Google Gemini's compatible endpoint, Groq, OpenRouter,
/// DeepSeek, Mistral, Ollama) and Anthropic's messages format (Claude).
/// </summary>
internal sealed class HttpAiChatClient(IHttpClientFactory http) : IAiChatClient
{
    public const string HttpClientName = "ai";

    public const int MaxRetryWaitSeconds = 30;

    public async Task<AiResponse> CompleteAsync(AiEndpoint endpoint, AiRequest request, CancellationToken ct)
    {
        var main = request.Vision ? endpoint.VisionModel ?? endpoint.Model : endpoint.Model;
        var models = new[] { main }.Concat(endpoint.FallbackModels ?? []).Distinct().ToList();
        for (var i = 0; ; i++)
        {
            try
            {
                return await SendAsync(endpoint, models[i], request, ct);
            }
            catch (AiProviderException ex) when (ex.Busy && i + 1 < models.Count)
            {
                // Out of this model's allowance or overloaded: the next model has its own.
            }
        }
    }

    private async Task<AiResponse> SendAsync(AiEndpoint endpoint, string model, AiRequest request, CancellationToken ct)
    {
        var (url, body) = endpoint.Kind == AiProviderKind.Anthropic ? Anthropic.Build(endpoint, model, request) : OpenAi.Build(endpoint, model, request);
        var payload = body.ToJsonString();
        // Free tiers allow a few requests a minute and are sometimes overloaded: wait as asked and try once more.
        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            if (endpoint.Kind == AiProviderKind.Anthropic)
            {
                message.Headers.Add("x-api-key", endpoint.ApiKey ?? "");
                message.Headers.Add("anthropic-version", "2023-06-01");
            }
            else if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.CreateClient(HttpClientName).SendAsync(message, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AiProviderException($"Could not reach {endpoint.Name}: {ex.Message}");
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                var status = (int)response.StatusCode;
                if (status is 429 or 503 or 529 && attempt == 1 && RetryWait(response, text, status) is { } wait)
                {
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new AiProviderException($"{endpoint.Name} answered {status}: {Short(text)}", status is 429 or 503 or 529);
                var json = JsonNode.Parse(text) ?? throw new AiProviderException($"{endpoint.Name} sent an empty answer.");
                return endpoint.Kind == AiProviderKind.Anthropic ? Anthropic.Parse(json) : OpenAi.Parse(json);
            }
        }
    }

    /// <summary>How long the provider asks us to wait (Retry-After header, or Google's "retryDelay": "17s"); null if longer than we are willing to wait.</summary>
    internal static TimeSpan? RetryWait(HttpResponseMessage response, string body, int status)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        if (seconds is null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(body, @"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""");
            if (m.Success) seconds = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        seconds ??= status == 429 ? 20 : 3;
        return seconds <= MaxRetryWaitSeconds ? TimeSpan.FromSeconds(Math.Max(1, seconds.Value)) : null;
    }

    private static string Short(string text)
    {
        try
        {
            // Most providers send {"error": {...}}; Google sends the same object inside a list.
            var node = JsonNode.Parse(text);
            if (node is JsonArray { Count: > 0 } list) node = list[0];
            var error = node is JsonObject obj ? obj["error"] : null;
            var msg = (error is JsonObject e ? e["message"]?.ToString() : error?.ToString()) ?? text;
            return msg.Length > 300 ? msg[..300] : msg;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return text.Length > 300 ? text[..300] : text;
        }
    }

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path;

    private static class OpenAi
    {
        public static (string, JsonObject) Build(AiEndpoint e, string model, AiRequest r)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = r.System } };
            foreach (var m in r.Messages)
            {
                var node = new JsonObject { ["role"] = m.Role };
                if (m.Image is { } img)
                    node["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = m.Text ?? "" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{Convert.ToBase64String(img.Data)}" }
                        }
                    };
                else node["content"] = m.Text ?? "";
                if (m.ToolCalls is { Count: > 0 } calls)
                    node["tool_calls"] = new JsonArray(calls.Select(c =>
                    {
                        var call = new JsonObject
                        {
                            ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments }
                        };
                        if (c.ProviderData is not null) call["extra_content"] = JsonNode.Parse(c.ProviderData);
                        return (JsonNode)call;
                    }).ToArray());
                if (m.ToolCallId is not null) node["tool_call_id"] = m.ToolCallId;
                messages.Add(node);
            }
            var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = r.MaxTokens, ["temperature"] = 0.2 };
            if (r.Tools is { Count: > 0 } tools)
                body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.ParametersSchema) }
                }).ToArray());
            if (r.Json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
            return (Join(e.BaseUrl, "chat/completions"), body);
        }

        public static AiResponse Parse(JsonNode json)
        {
            var message = json["choices"]?[0]?["message"] ?? throw new AiProviderException("The AI answer had no message.");
            var truncated = json["choices"]?[0]?["finish_reason"]?.GetValue<string>() == "length";
            var calls = (message["tool_calls"] as JsonArray ?? []).Select((c, i) =>
            {
                var args = c?["function"]?["arguments"];
                return new AiToolCall(c?["id"]?.GetValue<string>() ?? $"call_{i}", c?["function"]?["name"]?.GetValue<string>() ?? "",
                    args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}", c?["extra_content"]?.ToJsonString());
            }).ToList();
            var content = message["content"] is JsonValue cv && cv.TryGetValue<string>(out var text) ? text : null;
            return new AiResponse(content, calls, json["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, json["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0,
                Truncated: truncated);
        }
    }

    private static class Anthropic
    {
        public static (string, JsonObject) Build(AiEndpoint e, string model, AiRequest r)
        {
            var messages = new JsonArray();
            JsonArray? pendingResults = null;
            foreach (var m in r.Messages)
            {
                if (m.Role == "tool")
                {
                    // Claude takes all the results of one turn in a single user message.
                    if (pendingResults is null)
                    {
                        pendingResults = [];
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = pendingResults });
                    }
                    pendingResults.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Text ?? "" });
                    continue;
                }
                pendingResults = null;
                var content = new JsonArray();
                if (m.Image is { } img)
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.MediaType, ["data"] = Convert.ToBase64String(img.Data) }
                    });
                if (!string.IsNullOrEmpty(m.Text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                foreach (var c in m.ToolCalls ?? [])
                    content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = ParseArgs(c.Arguments) });
                messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
            }
            var system = r.Json ? r.System + "\nReply with a single JSON object only." : r.System;
            var body = new JsonObject { ["model"] = model, ["max_tokens"] = r.MaxTokens, ["system"] = system, ["messages"] = messages, ["temperature"] = 0.2 };
            if (r.Tools is { Count: > 0 } tools)
                body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(t.ParametersSchema)
                }).ToArray());
            var trimmed = e.BaseUrl.TrimEnd('/');
            return (trimmed.EndsWith("/v1", StringComparison.Ordinal) ? trimmed + "/messages" : trimmed + "/v1/messages", body);
        }

        public static AiResponse Parse(JsonNode json)
        {
            var text = new StringBuilder();
            var calls = new List<AiToolCall>();
            foreach (var block in json["content"] as JsonArray ?? [])
            {
                var type = block?["type"]?.GetValue<string>();
                if (type == "text") text.Append(block!["text"]?.GetValue<string>());
                else if (type == "tool_use")
                    calls.Add(new AiToolCall(block!["id"]!.GetValue<string>(), block["name"]!.GetValue<string>(), block["input"]?.ToJsonString() ?? "{}"));
            }
            return new AiResponse(text.Length > 0 ? text.ToString() : null, calls, json["usage"]?["input_tokens"]?.GetValue<int>() ?? 0,
                json["usage"]?["output_tokens"]?.GetValue<int>() ?? 0, Truncated: json["stop_reason"]?.GetValue<string>() == "max_tokens");
        }

        private static JsonNode ParseArgs(string args)
        {
            try
            {
                return JsonNode.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }
    }
}

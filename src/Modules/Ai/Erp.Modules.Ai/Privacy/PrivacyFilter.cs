using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Ai.Privacy;

/// <summary>
/// Keeps personal data away from AI providers that are not private (a shared free tier keeps what it is sent). Names under
/// <see cref="AiPrivacy.PersonalKeys"/> and anything that looks like a QID, IBAN, phone number or email become placeholders
/// ([P1], [P2]…) on the way out and are put back in the answer. When the provider is private the filter passes everything through.
/// </summary>
internal sealed partial class PrivacyFilter(bool active)
{
    public const int MaxJsonLength = 12_000;
    private readonly Dictionary<string, string> _tokenByValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _valueByToken = new(StringComparer.Ordinal);

    public bool Active => active;

    /// <summary>Free text going out (a question, a fact): identifiers are replaced.</summary>
    public string Text(string text) => active ? Identifiers().Replace(text, m => Token(m.Value)) : text;

    /// <summary>A value going out that is personal as a whole (a name).</summary>
    public string Personal(string value) => active && !string.IsNullOrWhiteSpace(value) ? Token(value) : value;

    /// <summary>A tool result going out, as compact JSON (cut short if very long).</summary>
    public string Json(object result)
    {
        var node = JsonSerializer.SerializeToNode(result, JsonOptions);
        if (active && node is not null) Walk(node);
        var json = node?.ToJsonString(JsonOptions) ?? "null";
        if (active) json = Text(json);
        return json.Length > MaxJsonLength ? json[..MaxJsonLength] + "…(cut)" : json;
    }

    /// <summary>Text coming back: placeholders become the real values again; any the model made up are dropped.</summary>
    public string Restore(string text) =>
        Placeholder().Replace(text, m => _valueByToken.TryGetValue(m.Value.TrimEnd(), out var value) ? value + m.Value[m.Value.TrimEnd().Length..] : "");

    /// <summary>The sentence telling the model about placeholders, only when there are any.</summary>
    public string PlaceholderRule =>
        active ? "Text like [P1] stands for a name that is hidden for privacy: copy it exactly, never guess or translate it." : "";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && IsPersonalKey(key)) obj[key] = Token(s);
                    else if (value is not null) Walk(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    if (item is not null) Walk(item);
                break;
        }
    }

    /// <summary>customer, customer_ar, staff_name…</summary>
    public static bool IsPersonalKey(string key) =>
        AiPrivacy.PersonalKeys.Contains(key) || AiPrivacy.PersonalKeys.Any(k => key.EndsWith("_" + k, StringComparison.OrdinalIgnoreCase)
                                                                         || key.StartsWith(k + "_", StringComparison.OrdinalIgnoreCase));

    private string Token(string value)
    {
        if (_tokenByValue.TryGetValue(value, out var token)) return token;
        token = $"[P{_tokenByValue.Count + 1}]";
        _tokenByValue[value] = token;
        _valueByToken[token] = value;
        return token;
    }

    // QID (11 digits from 2/3), IBAN, email, Qatar phone (+974 optional, 8 digits from 3/5/6/7, maybe written 4 + 4).
    [GeneratedRegex(@"\b[23]\d{10}\b|\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b|[\w.+-]+@[\w-]+\.[\w.-]+|(?:\+?974[\s-]?)?\b[3567]\d{3}[\s-]?\d{4}\b")]
    private static partial Regex Identifiers();

    [GeneratedRegex(@"\[P\d+\] ?")]
    private static partial Regex Placeholder();
}

using System.Globalization;
using System.Text.Json;

namespace Erp.SharedKernel.Application;

/// <summary>
/// A read-only question the AI assistant may ask the ERP ("customer balances", "profit and loss for a period"). Modules offer tools
/// over their existing reports; the assistant only sees tools the user holds <see cref="Permission"/> for and whose module is on.
/// Tools never change data. Values under the keys in <see cref="AiPrivacy.PersonalKeys"/> are hidden from shared AI providers.
/// </summary>
public interface IAiTool
{
    /// <summary>snake_case, unique.</summary>
    string Name { get; }

    /// <summary>English, for the model: what the tool answers and when to use it.</summary>
    string Description { get; }

    string Permission { get; }

    string? Module => null;

    /// <summary>JSON schema of the arguments (an object).</summary>
    string ParametersSchema => AiArgs.NoParameters;

    Task<object> InvokeAsync(JsonElement args, CancellationToken ct);
}

public static class AiPrivacy
{
    /// <summary>
    /// Keys whose values name a person or a business. Tools put names under these keys; before tool results reach a provider that is not
    /// private (a shared free tier), their values are swapped for placeholders like [P1] and swapped back in the answer.
    /// </summary>
    public static readonly IReadOnlySet<string> PersonalKeys =
        new HashSet<string>(["customer", "supplier", "employee", "name", "person", "owner", "phone", "email", "contact"], StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reading tool arguments the model sent (it may send numbers as strings, or leave optional ones out).</summary>
public static class AiArgs
{
    public const string NoParameters = """{"type":"object","properties":{}}""";

    public static string? String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    public static int Int(JsonElement args, string name, int fallback, int min, int max) =>
        int.TryParse(String(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;

    public static DateOnly Date(JsonElement args, string name, DateOnly fallback) =>
        DateOnly.TryParseExact(String(args, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : fallback;
}

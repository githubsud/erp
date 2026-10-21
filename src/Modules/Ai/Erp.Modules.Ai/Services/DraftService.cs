using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Modules.Ai.Privacy;
using Erp.Modules.Ai.Providers;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.Ai.Services;

public sealed record DraftRequest(string Kind, string? Language, IReadOnlyDictionary<string, string>? Facts, string? Instructions, string? Phone);

public sealed record DraftResponse(string Text, string? WhatsAppUrl, string Provider);

public sealed record DocumentFields(string? NameEn, string? NameAr, string? IdNumber, string? PassportNumber, string? Nationality, string? DateOfBirth,
    string? ExpiryDate, string? Gender, string? Occupation);

/// <summary>Short messages to customers (payment reminders, "your order is ready"), written by the AI for a person to check and send by WhatsApp.</summary>
internal sealed partial class DraftService(AiGateway gateway, ICompanyProfileProvider company)
{
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["payment_reminder"] = "a polite reminder that an invoice is unpaid: mention the amount and the due date, ask them to pay or reply if already paid",
        ["order_ready"] = "telling the customer their order is ready for collection: mention the order number and the balance to pay, if any",
        ["appointment_reminder"] = "reminding the customer of their appointment: mention the date and time, ask them to reply to confirm or change",
        ["document_renewal"] = "reminding someone that a document or licence expires soon and needs renewing: mention the document and the expiry date",
        ["thank_you"] = "thanking the customer for their visit or purchase and inviting them back",
        ["custom"] = "the message the user describes in the instructions"
    };

    public async Task<DraftResponse> DraftAsync(DraftRequest r, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(r.Kind ?? "", out var purpose))
            throw new BusinessRuleException("ai.draft_kind", "Unknown kind of message.", "نوع الرسالة غير معروف.");
        if (r.Kind == "custom" && string.IsNullOrWhiteSpace(r.Instructions))
            throw new BusinessRuleException("ai.draft_instructions", "Describe the message you want.", "صف الرسالة التي تريدها.");
        var endpoint = await gateway.ResolveAsync(ct);
        var filter = new PrivacyFilter(!endpoint.IsPrivate);
        var profile = await company.GetAsync(ct);
        var ar = r.Language != "en";
        var facts = new StringBuilder();
        facts.Append("- business: ").Append(filter.Personal(ar ? profile.NameAr : profile.NameEn)).Append('\n');
        foreach (var (key, value) in r.Facts ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(value) || key.Length > 40) continue;
            var personal = PrivacyFilter.IsPersonalKey(key);
            facts.Append("- ").Append(key).Append(": ").Append(personal ? filter.Personal(value.Trim()) : filter.Text(Limit(value.Trim(), 200))).Append('\n');
        }
        var system = $"""
            You write one WhatsApp message from a small business in Qatar to a customer, in {(ar ? "Arabic (clear Modern Standard Arabic, friendly Gulf tone)" : "English")}.
            The message is {purpose}. At most 60 words, warm and respectful, no subject line, no markdown, at most one emoji.
            Use only the facts given; amounts are in QAR. Sign off with the business name. {filter.PlaceholderRule}
            """;
        var user = $"Facts:\n{facts}" + (string.IsNullOrWhiteSpace(r.Instructions) ? "" : $"\nInstructions: {filter.Text(Limit(r.Instructions.Trim(), 500))}");
        var response = await gateway.CompleteAsync(endpoint, new AiRequest(system, [AiMessage.User(user)], MaxTokens: 400), ct);
        var text = filter.Restore(response.Text?.Trim().Trim('"') ?? "");
        return new DraftResponse(text, WhatsAppUrl(r.Phone, text), (response.AnsweredBy ?? endpoint).Name);
    }

    /// <summary>wa.me link with the text filled in; an 8-digit number is taken as a Qatar number. No phone: WhatsApp asks whom to send to.</summary>
    public static string WhatsAppUrl(string? phone, string text)
    {
        var digits = NonDigits().Replace(phone ?? "", "");
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.Length == 8) digits = "974" + digits;
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(text)}";
    }

    private static string Limit(string s, int max) => s.Length > max ? s[..max] : s;

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();
}

/// <summary>
/// Reads the fields of a QID or passport photo. The photo is personal data, so this only runs on a private model (the company's own
/// subscription or a model on our server) that can read images. The photo is not stored.
/// </summary>
internal sealed partial class DocumentReader(AiGateway gateway)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private static readonly string[] Types = ["image/jpeg", "image/png", "image/webp"];

    public async Task<DocumentFields> ReadAsync(string kind, string mediaType, byte[] image, CancellationToken ct)
    {
        if (kind is not ("qid" or "passport"))
            throw new BusinessRuleException("ai.document_kind", "Choose QID or passport.", "اختر البطاقة الشخصية أو جواز السفر.");
        if (!Types.Contains(mediaType) || image.Length is 0 or > MaxBytes)
            throw new BusinessRuleException("ai.document_image", "Upload a JPG, PNG or WEBP photo up to 5 MB.", "ارفع صورة JPG أو PNG أو WEBP لا تزيد عن 5 ميغابايت.");
        var endpoint = await gateway.ResolveAsync(ct);
        if (!endpoint.IsPrivate)
            throw new BusinessRuleException("ai.document_private", "Reading ID photos needs a private AI: connect your own AI subscription in AI settings.",
                "قراءة صور الهوية تحتاج ذكاءً اصطناعياً خاصاً: اربط اشتراكك الخاص من إعدادات الذكاء الاصطناعي.");
        if (string.IsNullOrWhiteSpace(endpoint.VisionModel))
            throw new BusinessRuleException("ai.document_vision", "The chosen AI model cannot read images. Choose a model that can (vision model) in AI settings.",
                "نموذج الذكاء الاصطناعي المختار لا يقرأ الصور. اختر نموذجاً يقرأ الصور من إعدادات الذكاء الاصطناعي.");
        var system = $$"""
            You read a photo of a {{(kind == "qid" ? "Qatar ID card (QID)" : "passport")}} and return its fields as one JSON object with these keys:
            name_en, name_ar, id_number, passport_number, nationality, date_of_birth, expiry_date, gender, occupation.
            Dates as yyyy-MM-dd, gender "M" or "F", nationality as the country name in English. Use null for anything you cannot read clearly. Never guess.
            """;
        var response = await gateway.CompleteAsync(endpoint, new AiRequest(system, [AiMessage.User("Read this document.", new AiImage(mediaType, image))],
            Vision: true, MaxTokens: 500, Json: true), ct);
        return Parse(response.Text ?? "");
    }

    public static DocumentFields Parse(string text)
    {
        var match = JsonObject().Match(text);
        if (!match.Success)
            throw new BusinessRuleException("ai.document_unreadable", "The document could not be read. Try a clearer photo.", "تعذرت قراءة المستند. جرّب صورة أوضح.");
        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ToString() is { Length: > 0 } s
                && !s.Equals("null", StringComparison.OrdinalIgnoreCase) ? s.Trim() : null;
            string? Date(string name) => DateOnly.TryParse(Get(name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd") : null;
            var gender = Get("gender")?.ToUpperInvariant() switch { "M" or "MALE" => "M", "F" or "FEMALE" => "F", _ => null };
            return new DocumentFields(Get("name_en"), Get("name_ar"), Get("id_number"), Get("passport_number"), Get("nationality"), Date("date_of_birth"),
                Date("expiry_date"), gender, Get("occupation"));
        }
        catch (JsonException)
        {
            throw new BusinessRuleException("ai.document_unreadable", "The document could not be read. Try a clearer photo.", "تعذرت قراءة المستند. جرّب صورة أوضح.");
        }
    }

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObject();
}

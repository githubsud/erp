using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Infrastructure.Outbox;
using Erp.SharedKernel.Application;

namespace Erp.Modules.HR.Notifications;

/// <summary>Builds short bilingual emails: Arabic block first (right-to-left), English below.</summary>
internal static class BilingualEmail
{
    public static EmailMessage Create(IReadOnlyList<string> to, string subjectEn, string subjectAr, string[] linesEn, string[] linesAr)
    {
        static string Encode(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:'IBM Plex Sans Arabic',Tahoma,Arial,sans-serif;font-size:14px;color:#1d2524\">");
        html.Append("<div dir=\"rtl\" style=\"text-align:right\">");
        html.Append($"<h3 style=\"color:#0F5C4D;margin:0 0 8px\">{Encode(subjectAr)}</h3>");
        foreach (var l in linesAr) html.Append($"<p style=\"margin:4px 0\">{Encode(l)}</p>");
        html.Append("</div><hr style=\"border:none;border-top:1px solid #d5dcda;margin:16px 0\"><div dir=\"ltr\">");
        html.Append($"<h3 style=\"color:#0F5C4D;margin:0 0 8px\">{Encode(subjectEn)}</h3>");
        foreach (var l in linesEn) html.Append($"<p style=\"margin:4px 0\">{Encode(l)}</p>");
        html.Append("</div></div>");
        var text = $"{subjectAr}\n{string.Join("\n", linesAr)}\n\n{subjectEn}\n{string.Join("\n", linesEn)}";
        return new EmailMessage(to, $"{subjectAr} | {subjectEn}", html.ToString(), text);
    }

    public static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : e.TryGetProperty(name, out v) ? v.ToString() : "";
}

internal sealed class DocumentExpiryEmailHandler(IUserDirectory users, IEmailSender email) : IOutboxHandler
{
    public string MessageType => "hr.document_expiry_reminder";

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        var to = await users.EmailsWithPermissionAsync(HrPermissions.DocumentsManage, ct);
        var days = payload.GetProperty("daysLeft").GetInt32();
        var docType = BilingualEmail.Str(payload, "docType");
        var (typeEn, typeAr) = DocumentNames.Of(docType);
        var ownerEn = BilingualEmail.Str(payload, "ownerNameEn");
        var ownerAr = BilingualEmail.Str(payload, "ownerNameAr");
        var expiry = BilingualEmail.Str(payload, "expiryDate");
        var docNo = BilingualEmail.Str(payload, "docNo");

        var (subjectEn, subjectAr) = days < 0
            ? ($"Expired {-days} days ago: {typeEn} — {ownerEn}", $"منتهٍ منذ {-days} يوم: {typeAr} — {ownerAr}")
            : days == 0
                ? ($"Expires today: {typeEn} — {ownerEn}", $"ينتهي اليوم: {typeAr} — {ownerAr}")
                : ($"Expires in {days} days: {typeEn} — {ownerEn}", $"ينتهي خلال {days} يوم: {typeAr} — {ownerAr}");

        await email.SendAsync(BilingualEmail.Create(to, subjectEn, subjectAr,
            [$"Document: {typeEn} {docNo}", $"Holder: {ownerEn}", $"Expiry date: {expiry}", "Please start the renewal so that payroll and WPS are not affected."],
            [$"المستند: {typeAr} {docNo}", $"صاحب المستند: {ownerAr}", $"تاريخ الانتهاء: {expiry}", "يرجى البدء في التجديد حتى لا تتأثر الرواتب وحماية الأجور."]), ct);
    }
}

internal sealed class PayrollPostedEmailHandler(IUserDirectory users, IEmailSender email) : IOutboxHandler
{
    public string MessageType => "hr.payroll_posted";

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        var to = await users.EmailsWithPermissionAsync(HrPermissions.PayrollPost, ct);
        var runNo = BilingualEmail.Str(payload, "runNo");
        var period = $"{BilingualEmail.Str(payload, "year")}-{payload.GetProperty("month").GetInt32():D2}";
        var net = payload.GetProperty("totalNet").GetDecimal();
        var deadline = BilingualEmail.Str(payload, "wpsDeadline");
        await email.SendAsync(BilingualEmail.Create(to, $"Payroll {period} posted ({runNo})", $"تم ترحيل رواتب {period} ({runNo})",
            [$"Net salaries: QAR {net:N2}", $"Generate the WPS file and deposit salaries by {deadline}."],
            [$"صافي الرواتب: {net:N2} ريال", $"يرجى إنشاء ملف حماية الأجور وإيداع الرواتب قبل {deadline}."]), ct);
    }
}

internal sealed class WpsDeadlineMissedEmailHandler(IUserDirectory users, IEmailSender email) : IOutboxHandler
{
    public string MessageType => "hr.wps_deadline_missed";

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        var to = await users.EmailsWithPermissionAsync(HrPermissions.PayrollApprove, ct);
        var runNo = BilingualEmail.Str(payload, "runNo");
        await email.SendAsync(BilingualEmail.Create(to, $"WPS deadline missed for {runNo}", $"تجاوز موعد حماية الأجور للمسيرة {runNo}",
            [$"Deadline: {BilingualEmail.Str(payload, "wpsDeadline")}", $"Paid on: {BilingualEmail.Str(payload, "paymentDate")}",
                "Late WPS payments can lead to Ministry of Labour penalties and service suspension."],
            [$"الموعد النهائي: {BilingualEmail.Str(payload, "wpsDeadline")}", $"تاريخ الصرف: {BilingualEmail.Str(payload, "paymentDate")}",
                "قد يؤدي التأخر في صرف الأجور إلى مخالفات من وزارة العمل وإيقاف المعاملات."]), ct);
    }
}

internal static class DocumentNames
{
    public static (string En, string Ar) Of(string docType) => docType switch
    {
        "Qid" => ("QID", "البطاقة الشخصية"),
        "Passport" => ("Passport", "جواز السفر"),
        "ResidencePermit" => ("Residence permit", "تصريح الإقامة"),
        "WorkPermit" => ("Work permit", "تصريح العمل"),
        "HealthCard" => ("Health card", "البطاقة الصحية"),
        "DrivingLicence" => ("Driving licence", "رخصة القيادة"),
        "ProfessionalLicence" => ("Professional licence", "الترخيص المهني"),
        "CommercialRegistration" => ("Commercial Registration", "السجل التجاري"),
        "CommercialLicense" => ("Commercial License", "الرخصة التجارية"),
        "MunicipalityLicense" => ("Municipality license", "رخصة البلدية"),
        "EstablishmentCard" => ("Establishment card", "بطاقة المنشأة"),
        "ChamberCertificate" => ("Chamber membership", "عضوية غرفة قطر"),
        "TaxCard" => ("Tax card", "البطاقة الضريبية"),
        "CivilDefence" => ("Civil Defence certificate", "شهادة الدفاع المدني"),
        "Lease" => ("Lease", "عقد الإيجار"),
        "Insurance" => ("Insurance", "التأمين"),
        "VehicleRegistration" => ("Vehicle registration", "استمارة المركبة"),
        _ => (docType, docType)
    };
}

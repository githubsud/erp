using Erp.SharedKernel.Application;

namespace Erp.Modules.HR.Documents;

/// <summary>Employee and company documents expiring within 60 days or already expired.</summary>
internal sealed class ExpiringDocumentsScheduledReport(DocumentService documents, IClock clock) : IScheduledReport
{
    public string Key => "hr.documents_expiring";
    public string NameEn => "Documents expiring (60 days)";
    public string NameAr => "المستندات التي تنتهي خلال 60 يوماً";
    public string Permission => HrPermissions.DocumentsManage;

    public async Task<ReportFile> RenderAsync(CancellationToken ct)
    {
        // Sent by email to any address the owner chose: personal numbers are masked.
        var rows = (await documents.ExpiringAsync(60, ct)).Select(DocumentService.Masked).OrderBy(d => d.ExpiryDate).ToList();
        var content = CsvFile.Build(["Owner", "المالك", "Document", "Number", "Expiry", "Days left", "Status"],
            rows.Select(x => (IReadOnlyList<object?>)[x.OwnerNameEn, x.OwnerNameAr, x.DocType, x.DocNo, x.ExpiryDate, x.DaysLeft, x.Status.ToString()]));
        var expired = rows.Count(r => r.DaysLeft < 0);
        return new ReportFile($"documents_expiring_{clock.Today:yyyyMMdd}.csv", CsvFile.ContentType, content,
            $"{rows.Count} document(s) expiring within 60 days, {expired} already expired.",
            $"{rows.Count} مستند ينتهي خلال 60 يوماً، منها {expired} منتهية.");
    }
}

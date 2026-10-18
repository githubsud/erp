using System.Text.Json;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Documents;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Notifications;

public sealed record EmailPayslipsResult(int Queued, int WithoutEmail, int AlreadySent);

/// <summary>Queues one email per payslip (PDF attached) to employees who have an email address.</summary>
internal sealed class PayslipEmailService(ErpDbContext db, ITenantContext tenant, IOutbox outbox, IClock clock)
{
    public const string MessageType = "hr.payslip_email";

    public Task<EmailPayslipsResult> QueueAsync(Guid runId, bool resend, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var run = await db.Set<PayrollRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.CompanyId == companyId, token)
                ?? throw new NotFoundException("Payroll run", runId);
            if (run.Status is not (PayrollStatus.Posted or PayrollStatus.Paid))
                throw new BusinessRuleException("hr.payroll_status", "Payslips can be emailed once the payroll is posted.", "يمكن إرسال القسائم بعد ترحيل الرواتب.");

            var payslips = await db.Set<Payslip>().Where(p => p.PayrollRunId == runId).ToListAsync(token);
            var ids = payslips.Select(p => p.EmployeeId).ToList();
            var emails = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Email, token);

            int queued = 0, missing = 0, already = 0;
            foreach (var p in payslips)
            {
                if (string.IsNullOrWhiteSpace(emails.GetValueOrDefault(p.EmployeeId))) { missing++; continue; }
                if (p.EmailedAt is not null && !resend) { already++; continue; }
                outbox.Enqueue(MessageType, new { companyId, runId, payslipId = p.Id });
                p.EmailedAt = clock.UtcNow;
                queued++;
            }
            return new EmailPayslipsResult(queued, missing, already);
        }, ct);
}

internal sealed class PayslipEmailHandler(ErpDbContext db, ITenantContext tenant, HrPdfDocuments pdf, IEmailSender email) : IOutboxHandler
{
    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] MonthsAr = ["يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"];

    public string MessageType => PayslipEmailService.MessageType;

    public async Task HandleAsync(Guid messageId, JsonElement payload, CancellationToken ct)
    {
        tenant.Set(tenant.RequireTenantId(), payload.GetProperty("companyId").GetGuid(), null);
        var runId = payload.GetProperty("runId").GetGuid();
        var payslipId = payload.GetProperty("payslipId").GetGuid();

        var run = await db.Set<PayrollRun>().AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        var payslip = await db.Set<Payslip>().AsNoTracking().SingleAsync(p => p.Id == payslipId, ct);
        var to = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == payslip.EmployeeId).Select(e => e.Email).SingleAsync(ct);
        if (string.IsNullOrWhiteSpace(to)) return;

        var (content, fileName) = await pdf.PayslipsAsync(runId, payslipId, ct);
        var periodEn = $"{MonthsEn[run.Month - 1]} {run.Year}";
        var periodAr = $"{MonthsAr[run.Month - 1]} {run.Year}";
        var message = BilingualEmail.Create([to], $"Your payslip for {periodEn}", $"قسيمة راتبك لشهر {periodAr}",
            [$"Dear {payslip.NameEn},", $"Please find attached your payslip for {periodEn}. Net salary: QAR {payslip.Net:N2}."],
            [$"عزيزي/عزيزتي {payslip.NameAr}،", $"مرفق قسيمة راتبك لشهر {periodAr}. صافي الراتب: {payslip.Net:N2} ريال."]);
        await email.SendAsync(message with { Attachments = [new EmailAttachment(fileName, "application/pdf", content)] }, ct);
    }
}

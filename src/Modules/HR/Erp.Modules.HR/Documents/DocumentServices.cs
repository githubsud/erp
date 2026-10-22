using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.HR.Documents;

public sealed record EmployeeDocumentRequest(EmployeeDocumentType DocType, string? DocNo, DateOnly? IssueDate, DateOnly? ExpiryDate, string? Notes);

public sealed record CompanyDocumentRequest(CompanyDocumentType DocType, string? NameEn, string? NameAr, string? DocNo, DateOnly? IssueDate,
    DateOnly? ExpiryDate, Guid? ResponsibleUserId, string? Notes);

public enum ExpiryStatus { Valid, Expiring, Expired }

public sealed record ExpiringDocument(
    string Scope,
    Guid DocumentId,
    string DocType,
    string? DocNo,
    string OwnerNameEn,
    string OwnerNameAr,
    Guid? EmployeeId,
    DateOnly ExpiryDate,
    int DaysLeft,
    ExpiryStatus Status);

internal sealed class DocumentService(ErpDbContext db, ITenantContext tenant, IClock clock)
{
    public const char MaskChar = '•';

    /// <summary>Keeps the last four characters, like the employee screen does for QID and IBAN.</summary>
    public static string? Mask(string? value) =>
        value is null ? null : value.Length <= 4 ? new string(MaskChar, value.Length) : new string(MaskChar, value.Length - 4) + value[^4..];

    /// <summary>Employee document numbers masked (company licences are not personal and stay as they are).</summary>
    public static ExpiringDocument Masked(ExpiringDocument d) => d.Scope == "employee" ? d with { DocNo = Mask(d.DocNo) } : d;

    /// <summary>Days before expiry at which reminders are sent.</summary>
    public static readonly int[] ReminderMilestones = [90, 60, 30, 14, 7, 0];

    public async Task<IReadOnlyList<ExpiringDocument>> ExpiringAsync(int withinDays, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var today = clock.Today;
        var limit = today.AddDays(Math.Clamp(withinDays, 0, 366));

        var employeeDocs = await (from d in db.Set<EmployeeDocument>()
                                  join e in db.Set<Employee>() on d.EmployeeId equals e.Id
                                  where e.CompanyId == companyId && e.Status == EmployeeStatus.Active && d.ExpiryDate != null && d.ExpiryDate <= limit
                                  select new { d.Id, d.DocType, d.DocNo, e.NameEn, e.NameAr, EmployeeId = e.Id, Expiry = d.ExpiryDate!.Value })
            .ToListAsync(ct);
        var companyDocs = await db.Set<CompanyDocument>()
            .Where(d => d.CompanyId == companyId && d.ExpiryDate != null && d.ExpiryDate <= limit)
            .Select(d => new { d.Id, d.DocType, d.DocNo, d.NameEn, d.NameAr, Expiry = d.ExpiryDate!.Value })
            .ToListAsync(ct);

        return employeeDocs.Select(d => Create("employee", d.Id, d.DocType.ToString(), d.DocNo, d.NameEn, d.NameAr, d.EmployeeId, d.Expiry, today))
            .Concat(companyDocs.Select(d => Create("company", d.Id, d.DocType.ToString(), d.DocNo, d.NameEn, d.NameAr, null, d.Expiry, today)))
            .OrderBy(d => d.ExpiryDate).ThenBy(d => d.OwnerNameEn)
            .ToList();
    }

    public static ExpiringDocument Create(string scope, Guid id, string type, string? no, string en, string ar, Guid? employeeId,
        DateOnly expiry, DateOnly today)
    {
        var daysLeft = expiry.DayNumber - today.DayNumber;
        var status = daysLeft < 0 ? ExpiryStatus.Expired : daysLeft <= 90 ? ExpiryStatus.Expiring : ExpiryStatus.Valid;
        return new ExpiringDocument(scope, id, type, no, en, ar, employeeId, expiry, daysLeft, status);
    }

    public async Task<EmployeeDocument> AddEmployeeDocumentAsync(Guid employeeId, EmployeeDocumentRequest r, CancellationToken ct)
    {
        if (!await db.Set<Employee>().AnyAsync(e => e.Id == employeeId && e.CompanyId == tenant.RequireCompanyId(), ct))
            throw new NotFoundException("Employee", employeeId);
        ValidateDates(r.IssueDate, r.ExpiryDate);
        var doc = new EmployeeDocument { EmployeeId = employeeId };
        Apply(doc, r);
        db.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<EmployeeDocument> UpdateEmployeeDocumentAsync(Guid id, EmployeeDocumentRequest r, CancellationToken ct)
    {
        var doc = await db.Set<EmployeeDocument>().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Document", id);
        ValidateDates(r.IssueDate, r.ExpiryDate);
        Apply(doc, r);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<CompanyDocument> SaveCompanyDocumentAsync(Guid? id, CompanyDocumentRequest r, CancellationToken ct)
    {
        ValidateDates(r.IssueDate, r.ExpiryDate);
        var doc = id is null
            ? new CompanyDocument { CompanyId = tenant.RequireCompanyId(), NameEn = "", NameAr = "" }
            : await db.Set<CompanyDocument>().SingleOrDefaultAsync(d => d.Id == id && d.CompanyId == tenant.RequireCompanyId(), ct)
              ?? throw new NotFoundException("Company document", id);
        var (defaultEn, defaultAr) = DefaultNames(r.DocType);
        (doc.NameEn, doc.NameAr) = Bilingual.Require(r.NameEn ?? defaultEn, r.NameAr ?? defaultAr, "document name");
        doc.DocType = r.DocType;
        doc.DocNo = r.DocNo?.Trim();
        doc.IssueDate = r.IssueDate;
        doc.ExpiryDate = r.ExpiryDate;
        doc.ResponsibleUserId = r.ResponsibleUserId;
        doc.Notes = r.Notes;
        if (id is null) db.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    private static void Apply(EmployeeDocument doc, EmployeeDocumentRequest r)
    {
        doc.DocType = r.DocType;
        // Someone who sees the number masked (••••1234) and saves the form leaves it as it was.
        if (r.DocNo is null || !r.DocNo.Contains(MaskChar)) doc.DocNo = r.DocNo?.Trim();
        doc.IssueDate = r.IssueDate;
        doc.ExpiryDate = r.ExpiryDate;
        doc.Notes = r.Notes;
    }

    private static void ValidateDates(DateOnly? issue, DateOnly? expiry)
    {
        if (issue is not null && expiry is not null && expiry < issue)
            throw new BusinessRuleException("hr.document_dates", "Expiry date cannot be before the issue date.", "لا يمكن أن يسبق تاريخ الانتهاء تاريخ الإصدار.");
    }

    public static (string En, string Ar) DefaultNames(CompanyDocumentType type) => type switch
    {
        CompanyDocumentType.CommercialRegistration => ("Commercial Registration (MoCI)", "السجل التجاري"),
        CompanyDocumentType.CommercialLicense => ("Commercial License", "الرخصة التجارية"),
        CompanyDocumentType.MunicipalityLicense => ("Municipality (Baladiya) License", "رخصة البلدية"),
        CompanyDocumentType.EstablishmentCard => ("Establishment (Computer) Card", "بطاقة المنشأة (الكمبيوتر)"),
        CompanyDocumentType.ChamberCertificate => ("Qatar Chamber Membership", "شهادة عضوية غرفة قطر"),
        CompanyDocumentType.TaxCard => ("Tax Card", "البطاقة الضريبية"),
        CompanyDocumentType.CivilDefence => ("Civil Defence Certificate", "شهادة الدفاع المدني"),
        CompanyDocumentType.Lease => ("Lease Agreement", "عقد الإيجار"),
        CompanyDocumentType.Insurance => ("Insurance Policy", "وثيقة التأمين"),
        CompanyDocumentType.VehicleRegistration => ("Vehicle Registration (Istimara)", "استمارة المركبة"),
        _ => ("Document", "مستند")
    };
}

/// <summary>
/// Once a day (after 06:00 Doha) enqueues reminders for documents reaching a milestone (90/60/30/14/7/0 days)
/// for every active tenant. Delivery (email/WhatsApp) is an outbox handler, so it retries independently.
/// </summary>
internal sealed class DocumentExpiryJob(IServiceScopeFactory scopes, IClock clock, ILogger<DocumentExpiryJob> logger) : BackgroundService
{
    private DateOnly _lastRun = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EncryptOldNumbersAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Encrypting old document numbers failed.");
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                var nowQatar = clock.UtcNow.ToOffset(TimeSpan.FromHours(3));
                if (nowQatar.Hour >= 6 && clock.Today > _lastRun)
                {
                    await RunOnceAsync(stoppingToken);
                    _lastRun = clock.Today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Document expiry job failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Document numbers saved before the column was encrypted (V0211) are encrypted once; afterwards this finds nothing.</summary>
    public async Task<int> EncryptOldNumbersAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantCompany> companies;
        await using (var scope = scopes.CreateAsyncScope())
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().GetActiveCompaniesAsync(ct);

        var total = 0;
        foreach (var tenantId in companies.Select(c => c.TenantId).Distinct())
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, null);
            var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
            var ids = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM hr.employee_document WHERE tenant_id = {tenantId} AND doc_no_encrypted IS NOT NULL AND doc_no_encrypted NOT LIKE 'v1:%'
                """).ToListAsync(ct);
            if (ids.Count == 0) continue;
            foreach (var doc in await db.Set<EmployeeDocument>().Where(d => ids.Contains(d.Id)).ToListAsync(ct))
                db.Entry(doc).Property(d => d.DocNo).IsModified = true;
            total += await db.SaveChangesAsync(ct);
        }
        if (total > 0) logger.LogInformation("Encrypted {Count} employee document number(s).", total);
        return total;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantCompany> companies;
        await using (var scope = scopes.CreateAsyncScope())
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().GetActiveCompaniesAsync(ct);

        var total = 0;
        foreach (var company in companies)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(company.TenantId, company.CompanyId, null);
            var service = scope.ServiceProvider.GetRequiredService<DocumentService>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();

            var due = (await service.ExpiringAsync(DocumentService.ReminderMilestones.Max(), ct))
                .Where(d => DocumentService.ReminderMilestones.Contains(d.DaysLeft) || (d.DaysLeft < 0 && -d.DaysLeft % 7 == 0))
                .ToList();
            // Emails leave the system: the number is masked.
            foreach (var d in due)
                outbox.Enqueue("hr.document_expiry_reminder", DocumentService.Masked(d));
            await db.SaveChangesAsync(ct);
            total += due.Count;
        }
        return total;
    }
}

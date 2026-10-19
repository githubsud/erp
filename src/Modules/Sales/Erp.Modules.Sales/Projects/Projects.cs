using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Sales.Documents;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Projects;

public enum ProjectStatus { Active, Completed, Closed }

public enum CertificateStatus { Draft, Invoiced, Cancelled }

public sealed class Project : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public Guid CustomerId { get; set; }
    public decimal ContractValue { get; set; }
    public decimal RetentionPercent { get; set; }
    public decimal BudgetCost { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public string? Notes { get; set; }
    public List<BoqItem> Items { get; init; } = [];
}

public sealed class BoqItem : TenantEntity
{
    public Guid ProjectId { get; init; }
    public int LineNo { get; set; }
    public string? Section { get; set; }
    public required string DescriptionEn { get; set; }
    public required string DescriptionAr { get; set; }
    public required string Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ProgressCertificate : TenantEntity
{
    public Guid ProjectId { get; init; }
    public required string CertificateNo { get; init; }
    public DateOnly PeriodEnd { get; set; }
    public CertificateStatus Status { get; set; } = CertificateStatus.Draft;
    public decimal GrossToDate { get; set; }
    public decimal PreviousGross { get; set; }
    public decimal Gross { get; set; }
    public decimal Retention { get; set; }
    public decimal Net { get; set; }
    public Guid? SalesInvoiceId { get; set; }
    public string? Notes { get; set; }
    public List<ProgressLine> Lines { get; init; } = [];
}

public sealed class ProgressLine : TenantEntity
{
    public Guid ProgressCertificateId { get; init; }
    public Guid BoqItemId { get; init; }
    public decimal QuantityToDate { get; init; }
    public decimal PreviousQuantity { get; init; }
    public decimal AmountToDate { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("project", "sal");
        b.Property(x => x.RetentionPercent).AsQuantity();
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ProjectId);
    }
}

internal sealed class BoqItemConfiguration : IEntityTypeConfiguration<BoqItem>
{
    public void Configure(EntityTypeBuilder<BoqItem> b)
    {
        b.ToTable("boq_item", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.Rate).AsQuantity();
    }
}

internal sealed class ProgressCertificateConfiguration : IEntityTypeConfiguration<ProgressCertificate>
{
    public void Configure(EntityTypeBuilder<ProgressCertificate> b)
    {
        b.ToTable("progress_certificate", "sal");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ProgressCertificateId);
    }
}

internal sealed class ProgressLineConfiguration : IEntityTypeConfiguration<ProgressLine>
{
    public void Configure(EntityTypeBuilder<ProgressLine> b)
    {
        b.ToTable("progress_line", "sal");
        b.Property(x => x.QuantityToDate).AsQuantity();
        b.Property(x => x.PreviousQuantity).AsQuantity();
    }
}

public sealed record BoqItemRequest(string? Section, string? DescriptionEn, string? DescriptionAr, string Unit, decimal Quantity, decimal Rate);

public sealed record ProjectRequest(string Code, string? NameEn, string? NameAr, Guid CustomerId, decimal RetentionPercent, decimal BudgetCost, DateOnly? StartDate,
    DateOnly? EndDate, ProjectStatus Status, string? Notes, IReadOnlyList<BoqItemRequest>? Items = null);

public sealed record ProgressEntry(Guid BoqItemId, decimal QuantityToDate);

public sealed record CertificateRequest(DateOnly PeriodEnd, IReadOnlyList<ProgressEntry> Progress, string? Notes);

public sealed record RetentionReleaseRequest(DateOnly Date, decimal Amount);

public sealed record BoqItemDto(Guid Id, int LineNo, string? Section, string DescriptionEn, string DescriptionAr, string Unit, decimal Quantity, decimal Rate,
    decimal Amount, decimal CertifiedQuantity, decimal CertifiedAmount, decimal PercentComplete);

public sealed record ProjectDto(Guid Id, string Code, string NameEn, string NameAr, Guid CustomerId, string CustomerNameEn, string CustomerNameAr,
    decimal ContractValue, decimal RetentionPercent, decimal BudgetCost, DateOnly? StartDate, DateOnly? EndDate, string Status, string? Notes,
    IReadOnlyList<BoqItemDto> Items, decimal CertifiedToDate, decimal PercentComplete, decimal RetentionHeld, decimal RetentionReleased);

public sealed record CertificateLineDto(Guid BoqItemId, string DescriptionEn, string DescriptionAr, string Unit, decimal Rate, decimal ContractQuantity,
    decimal PreviousQuantity, decimal QuantityToDate, decimal AmountToDate, decimal Amount);

public sealed record CertificateDto(Guid Id, Guid ProjectId, string CertificateNo, DateOnly PeriodEnd, string Status, decimal GrossToDate, decimal PreviousGross,
    decimal Gross, decimal Retention, decimal Net, Guid? SalesInvoiceId, string? InvoiceNo, string? Notes, IReadOnlyList<CertificateLineDto> Lines);

public sealed record ProjectProfitLine(string Code, string NameEn, string NameAr, decimal Amount);

public sealed record ProjectProfit(Guid ProjectId, decimal ContractValue, decimal CertifiedToDate, decimal Revenue, decimal Cost, decimal Profit,
    decimal? MarginPercent, decimal BudgetCost, decimal CostToBudgetPercent, IReadOnlyList<ProjectProfitLine> Income, IReadOnlyList<ProjectProfitLine> Costs);

/// <summary>
/// Customer contracts. Each progress certificate records cumulative quantities done per BOQ item; the difference to the previous
/// certificate is billed as a sales invoice, less the retention percentage, which the customer pays at handover.
/// </summary>
internal sealed class ProjectService(ErpDbContext db, ITenantContext tenant, SalesInvoiceService invoices, IFinanceQueries finance)
{
    public Task<Project> SaveAsync(Guid? id, ProjectRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
            var customer = await CustomerService.ActiveCustomerAsync(db, tenant, r.CustomerId, token);
            if (customer.Currency != Money.BaseCurrency)
                throw new BusinessRuleException("sal.project_currency", "Projects are billed in QAR.", "تُفوتر المشاريع بالريال القطري.");
            if (r.RetentionPercent is < 0 or > 50)
                throw new BusinessRuleException("sal.retention_percent", "Retention must be between 0% and 50%.", "يجب أن تكون نسبة المحتجزات بين 0% و50%.");
            var project = id is null
                ? db.Add(new Project { CompanyId = tenant.RequireCompanyId(), Code = Guard.Required(r.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar }).Entity
                : await FindAsync(id.Value, token);
            project.NameEn = en;
            project.NameAr = ar;
            project.CustomerId = customer.Id;
            project.RetentionPercent = r.RetentionPercent;
            project.BudgetCost = Money.Round(r.BudgetCost);
            project.StartDate = r.StartDate;
            project.EndDate = r.EndDate;
            project.Status = r.Status;
            project.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
            if (r.Items is not null)
            {
                var certified = await db.Set<ProgressCertificate>().AnyAsync(c => c.ProjectId == project.Id && c.Status != CertificateStatus.Cancelled, token);
                if (certified)
                    throw new BusinessRuleException("sal.boq_locked", "The bill of quantities cannot change once work is certified; add variations as new items.",
                        "لا يمكن تعديل جدول الكميات بعد اعتماد أعمال؛ أضف التغييرات كبنود جديدة.");
                db.RemoveRange(project.Items);
                project.Items.Clear();
                await db.SaveChangesAsync(token);
                var no = 0;
                foreach (var i in r.Items)
                {
                    var (dEn, dAr) = Bilingual.Require(i.DescriptionEn, i.DescriptionAr);
                    if (i.Quantity <= 0 || i.Rate < 0)
                        throw new BusinessRuleException("sal.boq_values", "Quantities must be positive and rates not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");
                    var item = new BoqItem
                    {
                        ProjectId = project.Id, LineNo = ++no, Section = string.IsNullOrWhiteSpace(i.Section) ? null : i.Section.Trim(), DescriptionEn = dEn,
                        DescriptionAr = dAr, Unit = Guard.Required(i.Unit, "unit"), Quantity = i.Quantity, Rate = i.Rate, Amount = Money.Round(i.Quantity * i.Rate)
                    };
                    project.Items.Add(item);
                    db.Add(item);
                }
            }
            project.ContractValue = project.Items.Sum(i => i.Amount);
            await db.SaveChangesAsync(token);
            return project;
        }, ct);

    /// <summary>Adds variation items to a project that already has certified work.</summary>
    public Task<Project> AddItemsAsync(Guid id, IReadOnlyList<BoqItemRequest> items, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var project = await FindAsync(id, token);
            var no = project.Items.Count == 0 ? 0 : project.Items.Max(i => i.LineNo);
            foreach (var i in items)
            {
                var (dEn, dAr) = Bilingual.Require(i.DescriptionEn, i.DescriptionAr);
                var item = new BoqItem
                {
                    ProjectId = project.Id, LineNo = ++no, Section = i.Section, DescriptionEn = dEn, DescriptionAr = dAr, Unit = Guard.Required(i.Unit, "unit"),
                    Quantity = i.Quantity, Rate = i.Rate, Amount = Money.Round(i.Quantity * i.Rate)
                };
                project.Items.Add(item);
                db.Add(item);
            }
            project.ContractValue = project.Items.Sum(i => i.Amount);
            await db.SaveChangesAsync(token);
            return project;
        }, ct);

    public Task<ProgressCertificate> SaveCertificateAsync(Guid projectId, Guid? id, CertificateRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var project = await FindAsync(projectId, token);
            if (project.Status != ProjectStatus.Active)
                throw new BusinessRuleException("sal.project_closed", "Only active projects are certified.", "تُعتمد أعمال المشاريع النشطة فقط.");
            ProgressCertificate certificate;
            if (id is null)
            {
                if (await db.Set<ProgressCertificate>().AnyAsync(c => c.ProjectId == projectId && c.Status == CertificateStatus.Draft, token))
                    throw new ConflictException("sal.certificate_draft", "Finish the draft certificate first.", "أكمل الشهادة المسودة أولاً.");
                certificate = new ProgressCertificate
                {
                    ProjectId = project.Id, CertificateNo = $"{project.Code}-IPC-{await db.Set<ProgressCertificate>().CountAsync(c => c.ProjectId == projectId, token) + 1:D2}"
                };
                db.Add(certificate);
            }
            else
            {
                certificate = await db.Set<ProgressCertificate>().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId, token)
                              ?? throw new NotFoundException("Progress certificate", id);
                if (certificate.Status != CertificateStatus.Draft)
                    throw new ConflictException("sal.certificate_invoiced", "This certificate is already invoiced.", "تمت فوترة هذه الشهادة مسبقاً.");
                db.RemoveRange(certificate.Lines);
                certificate.Lines.Clear();
                await db.SaveChangesAsync(token);
            }
            var previous = await PreviousQuantitiesAsync(projectId, token);
            certificate.PeriodEnd = r.PeriodEnd;
            certificate.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
            foreach (var entry in r.Progress)
            {
                var item = project.Items.SingleOrDefault(i => i.Id == entry.BoqItemId) ?? throw new NotFoundException("BOQ item", entry.BoqItemId);
                var before = previous.GetValueOrDefault(item.Id);
                if (entry.QuantityToDate < before)
                    throw new BusinessRuleException("sal.progress_back", $"{item.DescriptionEn}: quantity to date cannot go below the {before:0.##} already certified.",
                        $"{item.DescriptionAr}: لا يمكن أن تقل الكمية حتى تاريخه عن {before:0.##} المعتمدة مسبقاً.");
                if (entry.QuantityToDate > item.Quantity)
                    throw new BusinessRuleException("sal.progress_over", $"{item.DescriptionEn}: more than the contract quantity of {item.Quantity:0.##}; add a variation.",
                        $"{item.DescriptionAr}: تتجاوز الكمية التعاقدية {item.Quantity:0.##}؛ أضف بند تغيير.");
                var toDate = entry.QuantityToDate == item.Quantity ? item.Amount : Money.Round(entry.QuantityToDate * item.Rate);
                var previousAmount = before == item.Quantity ? item.Amount : Money.Round(before * item.Rate);
                certificate.Lines.Add(new ProgressLine
                {
                    ProgressCertificateId = certificate.Id, BoqItemId = item.Id, QuantityToDate = entry.QuantityToDate, PreviousQuantity = before,
                    AmountToDate = toDate, Amount = toDate - previousAmount
                });
            }
            foreach (var line in certificate.Lines) db.Add(line);
            certificate.Gross = certificate.Lines.Sum(l => l.Amount);
            certificate.PreviousGross = await db.Set<ProgressCertificate>()
                .Where(c => c.ProjectId == projectId && c.Status == CertificateStatus.Invoiced).SumAsync(c => (decimal?)c.Gross, token) ?? 0;
            certificate.GrossToDate = certificate.PreviousGross + certificate.Gross;
            certificate.Retention = Money.Round(certificate.Gross * project.RetentionPercent / 100m);
            certificate.Net = certificate.Gross - certificate.Retention;
            await db.SaveChangesAsync(token);
            return certificate;
        }, ct);

    /// <summary>Bills the certificate: a posted sales invoice for this period's work, with the retention held back.</summary>
    public Task<ProgressCertificate> InvoiceAsync(Guid projectId, Guid id, DateOnly date, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var project = await FindAsync(projectId, token);
            var certificate = await db.Set<ProgressCertificate>().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId, token)
                              ?? throw new NotFoundException("Progress certificate", id);
            if (certificate.Status != CertificateStatus.Draft)
                throw new ConflictException("sal.certificate_invoiced", "This certificate is already invoiced.", "تمت فوترة هذه الشهادة مسبقاً.");
            var lines = certificate.Lines.Where(l => l.Amount > 0).ToList();
            if (lines.Count == 0) throw new BusinessRuleException("sal.certificate_empty", "No progress to bill in this certificate.", "لا توجد أعمال لفوترتها في هذه الشهادة.");
            var items = project.Items.ToDictionary(i => i.Id);
            var invoice = await invoices.CreateAsync(new InvoiceRequest(project.CustomerId, date, null, null, certificate.CertificateNo,
                $"{project.NameEn} — progress certificate {certificate.CertificateNo}",
                lines.Select(l => new InvoiceLineRequest(null, null, null,
                    $"{items[l.BoqItemId].DescriptionEn} · {items[l.BoqItemId].DescriptionAr} ({l.QuantityToDate - l.PreviousQuantity:0.###} {items[l.BoqItemId].Unit})",
                    1, l.Amount)).ToList()), token);
            invoice.ProjectId = project.Id;
            invoice.RetentionAmount = certificate.Retention;
            await db.SaveChangesAsync(token);
            await invoices.PostAsync(invoice.Id, token);
            certificate.SalesInvoiceId = invoice.Id;
            certificate.Status = CertificateStatus.Invoiced;
            await db.SaveChangesAsync(token);
            return certificate;
        }, ct);

    /// <summary>Bills retention held on earlier certificates (at handover or after the defects period).</summary>
    public Task<Guid> ReleaseRetentionAsync(Guid projectId, RetentionReleaseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var project = await FindAsync(projectId, token);
            var (held, released) = await RetentionAsync(projectId, token);
            var amount = Money.Round(r.Amount);
            if (amount <= 0 || amount > held - released)
                throw new BusinessRuleException("sal.retention_release", $"Only {held - released:N2} of retention is held.", $"المحتجز المتبقي {held - released:N2} فقط.");
            var invoice = await invoices.CreateAsync(new InvoiceRequest(project.CustomerId, r.Date, null, null, $"{project.Code}-RET",
                $"{project.NameEn} — release of retention", [new InvoiceLineRequest(null, null, null, $"Release of retention · الإفراج عن المحتجزات — {project.NameEn}", 1, amount)]),
                token);
            invoice.ProjectId = project.Id;
            foreach (var line in invoice.Lines) db.Entry(line).Property(nameof(SalesInvoiceLine.LineType)).CurrentValue = InvoiceLineType.RetentionRelease;
            await db.SaveChangesAsync(token);
            await invoices.PostAsync(invoice.Id, token);
            return invoice.Id;
        }, ct);

    public async Task<ProjectDto> GetAsync(Guid id, CancellationToken ct)
    {
        var project = await FindAsync(id, ct);
        var customer = await db.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == project.CustomerId, ct);
        var certified = await CertifiedQuantitiesAsync(id, ct);
        var (held, released) = await RetentionAsync(id, ct);
        var items = project.Items.OrderBy(i => i.LineNo).Select(i =>
        {
            var q = certified.GetValueOrDefault(i.Id);
            var amount = q == i.Quantity ? i.Amount : Money.Round(q * i.Rate);
            return new BoqItemDto(i.Id, i.LineNo, i.Section, i.DescriptionEn, i.DescriptionAr, i.Unit, i.Quantity, i.Rate, i.Amount, q, amount,
                i.Quantity == 0 ? 0 : Math.Round(q * 100m / i.Quantity, 1));
        }).ToList();
        var certifiedToDate = items.Sum(i => i.CertifiedAmount);
        return new ProjectDto(project.Id, project.Code, project.NameEn, project.NameAr, customer.Id, customer.NameEn, customer.NameAr, project.ContractValue,
            project.RetentionPercent, project.BudgetCost, project.StartDate, project.EndDate, project.Status.ToString(), project.Notes, items, certifiedToDate,
            project.ContractValue == 0 ? 0 : Math.Round(certifiedToDate * 100m / project.ContractValue, 1), held, released);
    }

    public async Task<List<CertificateDto>> CertificatesAsync(Guid projectId, Guid? id, CancellationToken ct)
    {
        var project = await FindAsync(projectId, ct);
        var items = project.Items.ToDictionary(i => i.Id);
        var list = await db.Set<ProgressCertificate>().AsNoTracking().Include(c => c.Lines)
            .Where(c => c.ProjectId == projectId && (id == null || c.Id == id)).OrderByDescending(c => c.PeriodEnd).ToListAsync(ct);
        var invoiceIds = list.Where(c => c.SalesInvoiceId != null).Select(c => c.SalesInvoiceId!.Value).ToList();
        var numbers = await db.Set<SalesInvoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.InvoiceNo, ct);
        return list.Select(c => new CertificateDto(c.Id, c.ProjectId, c.CertificateNo, c.PeriodEnd, c.Status.ToString(), c.GrossToDate, c.PreviousGross, c.Gross,
            c.Retention, c.Net, c.SalesInvoiceId, c.SalesInvoiceId is { } inv ? numbers.GetValueOrDefault(inv) : null, c.Notes,
            c.Lines.Select(l => new CertificateLineDto(l.BoqItemId, items[l.BoqItemId].DescriptionEn, items[l.BoqItemId].DescriptionAr, items[l.BoqItemId].Unit,
                items[l.BoqItemId].Rate, items[l.BoqItemId].Quantity, l.PreviousQuantity, l.QuantityToDate, l.AmountToDate, l.Amount))
                .OrderBy(l => items[l.BoqItemId].LineNo).ToList())).ToList();
    }

    /// <summary>Profit and loss of the project from ledger lines tagged with it (revenue billed, costs booked), against the cost budget.</summary>
    public async Task<ProjectProfit> ProfitAsync(Guid id, CancellationToken ct)
    {
        var project = await GetAsync(id, ct);
        var ledger = await finance.GetProjectLedgerAsync(id, ct);
        var income = ledger.Where(l => l.IsIncome).Select(l => new ProjectProfitLine(l.Code, l.NameEn, l.NameAr, -l.Net)).ToList();
        var costs = ledger.Where(l => !l.IsIncome).Select(l => new ProjectProfitLine(l.Code, l.NameEn, l.NameAr, l.Net)).ToList();
        var revenue = income.Sum(l => l.Amount);
        var cost = costs.Sum(l => l.Amount);
        return new ProjectProfit(id, project.ContractValue, project.CertifiedToDate, revenue, cost, revenue - cost,
            revenue == 0 ? null : Math.Round((revenue - cost) * 100m / revenue, 1), project.BudgetCost,
            project.BudgetCost == 0 ? 0 : Math.Round(cost * 100m / project.BudgetCost, 1), income, costs);
    }

    private async Task<Dictionary<Guid, decimal>> PreviousQuantitiesAsync(Guid projectId, CancellationToken ct) => await CertifiedQuantitiesAsync(projectId, ct);

    /// <summary>Latest quantity to date per BOQ item across invoiced certificates.</summary>
    private async Task<Dictionary<Guid, decimal>> CertifiedQuantitiesAsync(Guid projectId, CancellationToken ct)
    {
        var lines = await (from l in db.Set<ProgressLine>().AsNoTracking()
                           join c in db.Set<ProgressCertificate>() on l.ProgressCertificateId equals c.Id
                           where c.ProjectId == projectId && c.Status == CertificateStatus.Invoiced
                           select new { l.BoqItemId, l.QuantityToDate }).ToListAsync(ct);
        return lines.GroupBy(l => l.BoqItemId).ToDictionary(g => g.Key, g => g.Max(x => x.QuantityToDate));
    }

    private async Task<(decimal Held, decimal Released)> RetentionAsync(Guid projectId, CancellationToken ct)
    {
        var invoices2 = await db.Set<SalesInvoice>().AsNoTracking().Include(i => i.Lines)
            .Where(i => i.ProjectId == projectId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Reversed).ToListAsync(ct);
        return (invoices2.Sum(i => i.RetentionAmount), invoices2.SelectMany(i => i.Lines).Where(l => l.LineType == InvoiceLineType.RetentionRelease).Sum(l => l.Amount));
    }

    private async Task<Project> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<Project>().Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id && p.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Project", id);
}

public sealed record InvoiceCertificateRequest(DateOnly Date);

internal static class ProjectEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales/projects").WithTags("Projects").RequireAuthorization();
        g.MapGet("", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await (from p in db.Set<Project>().AsNoTracking()
                   join c in db.Set<Customer>() on p.CustomerId equals c.Id
                   where p.CompanyId == t.RequireCompanyId()
                   orderby p.Code
                   select new { p.Id, p.Code, p.NameEn, p.NameAr, CustomerNameEn = c.NameEn, CustomerNameAr = c.NameAr, p.ContractValue, p.RetentionPercent,
                       Status = p.Status.ToString(), p.StartDate, p.EndDate }).ToListAsync(ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/{id:guid}", (Guid id, ProjectService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("", async (ProjectRequest r, ProjectService s, CancellationToken ct) => await s.GetAsync((await s.SaveAsync(null, r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.ProjectsManage);
        g.MapPut("/{id:guid}", async (Guid id, ProjectRequest r, ProjectService s, CancellationToken ct) => await s.GetAsync((await s.SaveAsync(id, r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.ProjectsManage);
        g.MapPost("/{id:guid}/items", async (Guid id, List<BoqItemRequest> items, ProjectService s, CancellationToken ct) =>
            await s.GetAsync((await s.AddItemsAsync(id, items, ct)).Id, ct)).RequirePermission(SalesPermissions.ProjectsManage);
        g.MapGet("/{id:guid}/certificates", (Guid id, ProjectService s, CancellationToken ct) => s.CertificatesAsync(id, null, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/{id:guid}/certificates", async (Guid id, CertificateRequest r, ProjectService s, CancellationToken ct) =>
            (await s.CertificatesAsync(id, (await s.SaveCertificateAsync(id, null, r, ct)).Id, ct)).Single()).RequirePermission(SalesPermissions.ProjectsManage);
        g.MapPut("/{id:guid}/certificates/{certId:guid}", async (Guid id, Guid certId, CertificateRequest r, ProjectService s, CancellationToken ct) =>
            (await s.CertificatesAsync(id, (await s.SaveCertificateAsync(id, certId, r, ct)).Id, ct)).Single()).RequirePermission(SalesPermissions.ProjectsManage);
        g.MapPost("/{id:guid}/certificates/{certId:guid}/invoice", async (Guid id, Guid certId, InvoiceCertificateRequest r, ProjectService s, CancellationToken ct) =>
            (await s.CertificatesAsync(id, (await s.InvoiceAsync(id, certId, r.Date, ct)).Id, ct)).Single()).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapPost("/{id:guid}/release-retention", async (Guid id, RetentionReleaseRequest r, ProjectService s, CancellationToken ct) =>
            new { SalesInvoiceId = await s.ReleaseRetentionAsync(id, r, ct) }).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapGet("/{id:guid}/profit", (Guid id, ProjectService s, CancellationToken ct) => s.ProfitAsync(id, ct)).RequirePermission(SalesPermissions.ReportsRead);
    }
}

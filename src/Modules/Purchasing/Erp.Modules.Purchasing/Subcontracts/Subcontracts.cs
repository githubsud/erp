using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Purchasing.Documents;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Purchasing.Subcontracts;

public enum SubcontractStatus { Active, Completed, Closed }

public enum SubcontractCertificateStatus { Draft, Invoiced, Cancelled }

public sealed class Subcontract : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>The customer project (contract) these works belong to; certificate invoices charge their cost to it.</summary>
    public Guid? ProjectId { get; set; }
    public decimal ContractValue { get; set; }
    public decimal RetentionPercent { get; set; }
    public decimal TaxRetentionPercent { get; set; }
    public DateOnly? TaxClearanceReceivedOn { get; set; }
    public SubcontractStatus Status { get; set; } = SubcontractStatus.Active;
    public string? Notes { get; set; }
    public List<SubcontractItem> Items { get; init; } = [];
}

public sealed class SubcontractItem : TenantEntity
{
    public Guid SubcontractId { get; init; }
    public int LineNo { get; set; }
    public string? Section { get; set; }
    public required string DescriptionEn { get; set; }
    public required string DescriptionAr { get; set; }
    public required string Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
}

public sealed class SubcontractCertificate : TenantEntity
{
    public Guid SubcontractId { get; init; }
    public required string CertificateNo { get; init; }
    public DateOnly PeriodEnd { get; set; }
    public SubcontractCertificateStatus Status { get; set; } = SubcontractCertificateStatus.Draft;
    public decimal GrossToDate { get; set; }
    public decimal PreviousGross { get; set; }
    public decimal Gross { get; set; }
    public decimal Retention { get; set; }
    public decimal TaxRetention { get; set; }
    public decimal Net { get; set; }
    public Guid? SupplierInvoiceId { get; set; }
    public string? Notes { get; set; }
    public List<SubcontractProgress> Lines { get; init; } = [];
}

public sealed class SubcontractProgress : TenantEntity
{
    public Guid CertificateId { get; init; }
    public Guid ItemId { get; init; }
    public decimal QuantityToDate { get; init; }
    public decimal PreviousQuantity { get; init; }
    public decimal AmountToDate { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class SubcontractConfiguration : IEntityTypeConfiguration<Subcontract>
{
    public void Configure(EntityTypeBuilder<Subcontract> b)
    {
        b.ToTable("subcontract", "pur");
        b.Property(x => x.RetentionPercent).AsQuantity();
        b.Property(x => x.TaxRetentionPercent).AsQuantity();
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SubcontractId);
    }
}

internal sealed class SubcontractItemConfiguration : IEntityTypeConfiguration<SubcontractItem>
{
    public void Configure(EntityTypeBuilder<SubcontractItem> b)
    {
        b.ToTable("subcontract_item", "pur");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.Rate).AsQuantity();
    }
}

internal sealed class SubcontractCertificateConfiguration : IEntityTypeConfiguration<SubcontractCertificate>
{
    public void Configure(EntityTypeBuilder<SubcontractCertificate> b)
    {
        b.ToTable("subcontract_certificate", "pur");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CertificateId);
    }
}

internal sealed class SubcontractProgressConfiguration : IEntityTypeConfiguration<SubcontractProgress>
{
    public void Configure(EntityTypeBuilder<SubcontractProgress> b)
    {
        b.ToTable("subcontract_progress", "pur");
        b.Property(x => x.QuantityToDate).AsQuantity();
        b.Property(x => x.PreviousQuantity).AsQuantity();
    }
}

public sealed record SubcontractItemRequest(string? Section, string? DescriptionEn, string? DescriptionAr, string Unit, decimal Quantity, decimal Rate);

/// <summary><see cref="TaxRetentionPercent"/> defaults to the purchasing setting when omitted.</summary>
public sealed record SubcontractRequest(string Code, string? NameEn, string? NameAr, Guid SupplierId, Guid? ProjectId, decimal RetentionPercent,
    decimal? TaxRetentionPercent, SubcontractStatus Status, string? Notes, IReadOnlyList<SubcontractItemRequest>? Items = null);

public sealed record SubcontractProgressEntry(Guid ItemId, decimal QuantityToDate);

public sealed record SubcontractCertificateRequest(DateOnly PeriodEnd, IReadOnlyList<SubcontractProgressEntry> Progress, string? Notes);

public sealed record SubcontractInvoiceRequest(DateOnly Date, string? SupplierRef);

public sealed record SubcontractReleaseRequest(DateOnly Date, decimal Amount, string? SupplierRef);

public sealed record TaxClearanceRequest(DateOnly ReceivedOn, DateOnly ReleaseDate, string? SupplierRef);

public sealed record SubcontractItemDto(Guid Id, int LineNo, string? Section, string DescriptionEn, string DescriptionAr, string Unit, decimal Quantity,
    decimal Rate, decimal Amount, decimal CertifiedQuantity, decimal CertifiedAmount, decimal PercentComplete);

public sealed record SubcontractDto(Guid Id, string Code, string NameEn, string NameAr, Guid SupplierId, string SupplierNameEn, string SupplierNameAr,
    Guid? ProjectId, decimal ContractValue, decimal RetentionPercent, decimal TaxRetentionPercent, DateOnly? TaxClearanceReceivedOn, string Status,
    string? Notes, IReadOnlyList<SubcontractItemDto> Items, decimal CertifiedToDate, decimal PercentComplete, decimal RetentionHeld,
    decimal RetentionReleased, decimal TaxRetentionHeld, decimal TaxRetentionReleased);

public sealed record SubcontractCertificateLineDto(Guid ItemId, string DescriptionEn, string DescriptionAr, string Unit, decimal Rate, decimal ContractQuantity,
    decimal PreviousQuantity, decimal QuantityToDate, decimal AmountToDate, decimal Amount);

public sealed record SubcontractCertificateDto(Guid Id, Guid SubcontractId, string CertificateNo, DateOnly PeriodEnd, string Status, decimal GrossToDate,
    decimal PreviousGross, decimal Gross, decimal Retention, decimal TaxRetention, decimal Net, Guid? SupplierInvoiceId, string? InvoiceNo, string? Notes,
    IReadOnlyList<SubcontractCertificateLineDto> Lines);

/// <summary>
/// Subcontracts: what a subcontractor will do (BOQ), cumulative progress certificates billed as posted supplier invoices, with the
/// retention and the tax-clearance retention held back as liabilities. Releasing either makes it payable to the subcontractor.
/// </summary>
internal sealed class SubcontractService(ErpDbContext db, ITenantContext tenant, SupplierInvoiceService invoices)
{
    public Task<Subcontract> SaveAsync(Guid? id, SubcontractRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
            var supplier = await PurchaseOrderService.ActiveSupplierAsync(db, tenant, r.SupplierId, token);
            if (supplier.Currency != Money.BaseCurrency)
                throw new BusinessRuleException("pur.subcontract_currency", "Subcontracts are billed in QAR.", "تُفوتر عقود الباطن بالريال القطري.");
            if (supplier.SubjectToWht)
                throw new BusinessRuleException("pur.subcontract_wht", "Subcontracts with suppliers subject to withholding tax are not supported; use supplier invoices.",
                    "عقود الباطن مع موردين خاضعين لضريبة الاستقطاع غير مدعومة؛ استخدم فواتير الموردين.");
            if (r.RetentionPercent is < 0 or > 50)
                throw new BusinessRuleException("pur.retention_percent", "Retention must be between 0% and 50%.", "يجب أن تكون نسبة المحتجزات بين 0% و50%.");
            var settings = await db.Set<PurchaseSettings>().AsNoTracking().SingleAsync(s => s.CompanyId == supplier.CompanyId, token);
            var taxRetention = r.TaxRetentionPercent ?? settings.TaxClearanceRetentionPercent;
            if (taxRetention is < 0 or > 100 || r.RetentionPercent + taxRetention > 100)
                throw new BusinessRuleException("pur.tax_retention_percent", "Check the retention percentages.", "تحقق من نسب المحتجزات.");
            var contract = id is null
                ? db.Add(new Subcontract { CompanyId = supplier.CompanyId, Code = Guard.Required(r.Code, "code").ToUpperInvariant(), NameEn = en, NameAr = ar }).Entity
                : await FindAsync(id.Value, token);
            var certified = await db.Set<SubcontractCertificate>().AnyAsync(c => c.SubcontractId == contract.Id && c.Status != SubcontractCertificateStatus.Cancelled, token);
            if (certified && (contract.SupplierId != supplier.Id || contract.RetentionPercent != r.RetentionPercent || contract.TaxRetentionPercent != taxRetention))
                throw new BusinessRuleException("pur.subcontract_locked", "The subcontractor and retention rates cannot change once work is certified.",
                    "لا يمكن تغيير المقاول أو نسب المحتجزات بعد اعتماد أعمال.");
            contract.NameEn = en;
            contract.NameAr = ar;
            contract.SupplierId = supplier.Id;
            contract.ProjectId = r.ProjectId;
            contract.RetentionPercent = r.RetentionPercent;
            contract.TaxRetentionPercent = taxRetention;
            contract.Status = r.Status;
            contract.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
            if (r.Items is not null)
            {
                if (certified)
                    throw new BusinessRuleException("pur.boq_locked", "The bill of quantities cannot change once work is certified; add variations as new items.",
                        "لا يمكن تعديل جدول الكميات بعد اعتماد أعمال؛ أضف التغييرات كبنود جديدة.");
                db.RemoveRange(contract.Items);
                contract.Items.Clear();
                await db.SaveChangesAsync(token);
                AddItems(contract, r.Items);
            }
            contract.ContractValue = contract.Items.Sum(i => i.Amount);
            await db.SaveChangesAsync(token);
            return contract;
        }, ct);

    public Task<Subcontract> AddItemsAsync(Guid id, IReadOnlyList<SubcontractItemRequest> items, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var contract = await FindAsync(id, token);
            AddItems(contract, items);
            contract.ContractValue = contract.Items.Sum(i => i.Amount);
            await db.SaveChangesAsync(token);
            return contract;
        }, ct);

    private void AddItems(Subcontract contract, IReadOnlyList<SubcontractItemRequest> items)
    {
        var no = contract.Items.Count == 0 ? 0 : contract.Items.Max(i => i.LineNo);
        foreach (var i in items)
        {
            var (dEn, dAr) = Bilingual.Require(i.DescriptionEn, i.DescriptionAr);
            if (i.Quantity <= 0 || i.Rate < 0)
                throw new BusinessRuleException("pur.boq_values", "Quantities must be positive and rates not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");
            var item = new SubcontractItem
            {
                SubcontractId = contract.Id, LineNo = ++no, Section = string.IsNullOrWhiteSpace(i.Section) ? null : i.Section.Trim(), DescriptionEn = dEn,
                DescriptionAr = dAr, Unit = Guard.Required(i.Unit, "unit"), Quantity = i.Quantity, Rate = i.Rate, Amount = Money.Round(i.Quantity * i.Rate)
            };
            contract.Items.Add(item);
            db.Add(item);
        }
    }

    public Task<SubcontractCertificate> SaveCertificateAsync(Guid contractId, Guid? id, SubcontractCertificateRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var contract = await FindAsync(contractId, token);
            if (contract.Status != SubcontractStatus.Active)
                throw new BusinessRuleException("pur.subcontract_closed", "Only active subcontracts are certified.", "تُعتمد أعمال عقود الباطن النشطة فقط.");
            SubcontractCertificate certificate;
            if (id is null)
            {
                if (await db.Set<SubcontractCertificate>().AnyAsync(c => c.SubcontractId == contractId && c.Status == SubcontractCertificateStatus.Draft, token))
                    throw new ConflictException("pur.certificate_draft", "Finish the draft certificate first.", "أكمل الشهادة المسودة أولاً.");
                var count = await db.Set<SubcontractCertificate>().CountAsync(c => c.SubcontractId == contractId, token);
                certificate = new SubcontractCertificate { SubcontractId = contract.Id, CertificateNo = $"{contract.Code}-SC-{count + 1:D2}" };
                db.Add(certificate);
            }
            else
            {
                certificate = await db.Set<SubcontractCertificate>().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.SubcontractId == contractId, token)
                              ?? throw new NotFoundException("Subcontract certificate", id);
                if (certificate.Status != SubcontractCertificateStatus.Draft)
                    throw new ConflictException("pur.certificate_invoiced", "This certificate is already invoiced.", "تمت فوترة هذه الشهادة مسبقاً.");
                db.RemoveRange(certificate.Lines);
                certificate.Lines.Clear();
                await db.SaveChangesAsync(token);
            }
            var previous = await CertifiedQuantitiesAsync(contractId, token);
            certificate.PeriodEnd = r.PeriodEnd;
            certificate.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
            foreach (var entry in r.Progress)
            {
                var item = contract.Items.SingleOrDefault(i => i.Id == entry.ItemId) ?? throw new NotFoundException("Subcontract item", entry.ItemId);
                var before = previous.GetValueOrDefault(item.Id);
                if (entry.QuantityToDate < before)
                    throw new BusinessRuleException("pur.progress_back", $"{item.DescriptionEn}: quantity to date cannot go below the {before:0.##} already certified.",
                        $"{item.DescriptionAr}: لا يمكن أن تقل الكمية حتى تاريخه عن {before:0.##} المعتمدة مسبقاً.");
                if (entry.QuantityToDate > item.Quantity)
                    throw new BusinessRuleException("pur.progress_over", $"{item.DescriptionEn}: more than the subcontract quantity of {item.Quantity:0.##}; add a variation.",
                        $"{item.DescriptionAr}: تتجاوز الكمية التعاقدية {item.Quantity:0.##}؛ أضف بند تغيير.");
                var toDate = entry.QuantityToDate == item.Quantity ? item.Amount : Money.Round(entry.QuantityToDate * item.Rate);
                var previousAmount = before == item.Quantity ? item.Amount : Money.Round(before * item.Rate);
                var line = new SubcontractProgress
                {
                    CertificateId = certificate.Id, ItemId = item.Id, QuantityToDate = entry.QuantityToDate, PreviousQuantity = before, AmountToDate = toDate,
                    Amount = toDate - previousAmount
                };
                certificate.Lines.Add(line);
                db.Add(line);
            }
            certificate.Gross = certificate.Lines.Sum(l => l.Amount);
            certificate.PreviousGross = await db.Set<SubcontractCertificate>()
                .Where(c => c.SubcontractId == contractId && c.Status == SubcontractCertificateStatus.Invoiced).SumAsync(c => (decimal?)c.Gross, token) ?? 0;
            certificate.GrossToDate = certificate.PreviousGross + certificate.Gross;
            certificate.Retention = Money.Round(certificate.Gross * contract.RetentionPercent / 100m);
            // Once the clearance certificate is in hand nothing more is held for tax.
            certificate.TaxRetention = contract.TaxClearanceReceivedOn is null ? Money.Round(certificate.Gross * contract.TaxRetentionPercent / 100m) : 0;
            certificate.Net = certificate.Gross - certificate.Retention - certificate.TaxRetention;
            await db.SaveChangesAsync(token);
            return certificate;
        }, ct);

    /// <summary>Books the certificate: a posted supplier invoice for this period's work (charged to the project), with both retentions held.</summary>
    public Task<SubcontractCertificate> InvoiceAsync(Guid contractId, Guid id, SubcontractInvoiceRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var contract = await FindAsync(contractId, token);
            var certificate = await db.Set<SubcontractCertificate>().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.SubcontractId == contractId, token)
                              ?? throw new NotFoundException("Subcontract certificate", id);
            if (certificate.Status != SubcontractCertificateStatus.Draft)
                throw new ConflictException("pur.certificate_invoiced", "This certificate is already invoiced.", "تمت فوترة هذه الشهادة مسبقاً.");
            var lines = certificate.Lines.Where(l => l.Amount > 0).ToList();
            if (lines.Count == 0) throw new BusinessRuleException("pur.certificate_empty", "No progress to bill in this certificate.", "لا توجد أعمال لفوترتها في هذه الشهادة.");
            var items = contract.Items.ToDictionary(i => i.Id);
            var invoice = await invoices.CreateAsync(new InvoiceRequest(contract.SupplierId, Ref(r.SupplierRef, certificate.CertificateNo), r.Date, null, null,
                $"{contract.NameEn} — certificate {certificate.CertificateNo}",
                lines.Select(l => new InvoiceLineRequest(InvoiceLineType.Service, null, null, null,
                    $"{items[l.ItemId].DescriptionEn} · {items[l.ItemId].DescriptionAr} ({l.QuantityToDate - l.PreviousQuantity:0.###} {items[l.ItemId].Unit})", 1,
                    l.Amount)).ToList(), Money.BaseCurrency, 1m, contract.ProjectId), token);
            invoice.SubcontractId = contract.Id;
            invoice.RetentionAmount = certificate.Retention;
            invoice.TaxRetentionAmount = certificate.TaxRetention;
            await db.SaveChangesAsync(token);
            await invoices.PostAsync(invoice.Id, token);
            certificate.SupplierInvoiceId = invoice.Id;
            certificate.Status = SubcontractCertificateStatus.Invoiced;
            await db.SaveChangesAsync(token);
            return certificate;
        }, ct);

    /// <summary>Makes held retention payable (handover or end of the defects period).</summary>
    public Task<Guid> ReleaseRetentionAsync(Guid contractId, SubcontractReleaseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var contract = await FindAsync(contractId, token);
            var held = await HeldAsync(contractId, token);
            var amount = Money.Round(r.Amount);
            if (amount <= 0 || amount > held.Retention - held.RetentionReleased)
                throw new BusinessRuleException("pur.retention_release", $"Only {held.Retention - held.RetentionReleased:N2} of retention is held.",
                    $"المحتجز المتبقي {held.Retention - held.RetentionReleased:N2} فقط.");
            return await ReleaseAsync(contract, InvoiceLineType.RetentionRelease, amount, r.Date, Ref(r.SupplierRef, $"{contract.Code}-RET"),
                $"Release of retention · الإفراج عن المحتجزات — {contract.NameEn}", token);
        }, ct);

    /// <summary>The subcontractor showed a tax clearance certificate: everything held for tax becomes payable and nothing more is held.</summary>
    public Task<Guid?> TaxClearanceAsync(Guid contractId, TaxClearanceRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var contract = await FindAsync(contractId, token);
            if (contract.TaxClearanceReceivedOn is not null)
                throw new ConflictException("pur.tax_clearance_recorded", "The tax clearance certificate is already recorded.", "تم تسجيل شهادة المخالصة الضريبية مسبقاً.");
            contract.TaxClearanceReceivedOn = r.ReceivedOn;
            var held = await HeldAsync(contractId, token);
            var amount = held.TaxRetention - held.TaxRetentionReleased;
            Guid? invoiceId = null;
            if (amount > 0)
                invoiceId = await ReleaseAsync(contract, InvoiceLineType.TaxRelease, amount, r.ReleaseDate, Ref(r.SupplierRef, $"{contract.Code}-TAX"),
                    $"Tax clearance received — release of tax retention · الإفراج عن محتجزات المخالصة الضريبية — {contract.NameEn}", token);
            await db.SaveChangesAsync(token);
            return invoiceId;
        }, ct);

    private async Task<Guid> ReleaseAsync(Subcontract contract, InvoiceLineType type, decimal amount, DateOnly date, string supplierRef, string description,
        CancellationToken ct)
    {
        var invoice = await invoices.CreateAsync(new InvoiceRequest(contract.SupplierId, supplierRef, date, null, null, description,
            [new InvoiceLineRequest(type, null, null, null, description, 1, amount)], Money.BaseCurrency, 1m, contract.ProjectId), true, ct);
        invoice.SubcontractId = contract.Id;
        await db.SaveChangesAsync(ct);
        await invoices.PostAsync(invoice.Id, ct);
        return invoice.Id;
    }

    public async Task<SubcontractDto> GetAsync(Guid id, CancellationToken ct)
    {
        var contract = await FindAsync(id, ct);
        var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == contract.SupplierId, ct);
        var certified = await CertifiedQuantitiesAsync(id, ct);
        var held = await HeldAsync(id, ct);
        var items = contract.Items.OrderBy(i => i.LineNo).Select(i =>
        {
            var q = certified.GetValueOrDefault(i.Id);
            var amount = q == i.Quantity ? i.Amount : Money.Round(q * i.Rate);
            return new SubcontractItemDto(i.Id, i.LineNo, i.Section, i.DescriptionEn, i.DescriptionAr, i.Unit, i.Quantity, i.Rate, i.Amount, q, amount,
                i.Quantity == 0 ? 0 : Math.Round(q * 100m / i.Quantity, 1));
        }).ToList();
        var certifiedToDate = items.Sum(i => i.CertifiedAmount);
        return new SubcontractDto(contract.Id, contract.Code, contract.NameEn, contract.NameAr, supplier.Id, supplier.NameEn, supplier.NameAr, contract.ProjectId,
            contract.ContractValue, contract.RetentionPercent, contract.TaxRetentionPercent, contract.TaxClearanceReceivedOn, contract.Status.ToString(),
            contract.Notes, items, certifiedToDate, contract.ContractValue == 0 ? 0 : Math.Round(certifiedToDate * 100m / contract.ContractValue, 1),
            held.Retention, held.RetentionReleased, held.TaxRetention, held.TaxRetentionReleased);
    }

    public async Task<List<SubcontractCertificateDto>> CertificatesAsync(Guid contractId, Guid? id, CancellationToken ct)
    {
        var contract = await FindAsync(contractId, ct);
        var items = contract.Items.ToDictionary(i => i.Id);
        var list = await db.Set<SubcontractCertificate>().AsNoTracking().Include(c => c.Lines)
            .Where(c => c.SubcontractId == contractId && (id == null || c.Id == id)).OrderByDescending(c => c.PeriodEnd).ThenByDescending(c => c.CertificateNo)
            .ToListAsync(ct);
        var invoiceIds = list.Where(c => c.SupplierInvoiceId != null).Select(c => c.SupplierInvoiceId!.Value).ToList();
        var numbers = await db.Set<SupplierInvoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.InvoiceNo, ct);
        return list.Select(c => new SubcontractCertificateDto(c.Id, c.SubcontractId, c.CertificateNo, c.PeriodEnd, c.Status.ToString(), c.GrossToDate,
            c.PreviousGross, c.Gross, c.Retention, c.TaxRetention, c.Net, c.SupplierInvoiceId,
            c.SupplierInvoiceId is { } inv ? numbers.GetValueOrDefault(inv) : null, c.Notes,
            c.Lines.Select(l => new SubcontractCertificateLineDto(l.ItemId, items[l.ItemId].DescriptionEn, items[l.ItemId].DescriptionAr, items[l.ItemId].Unit,
                items[l.ItemId].Rate, items[l.ItemId].Quantity, l.PreviousQuantity, l.QuantityToDate, l.AmountToDate, l.Amount))
                .OrderBy(l => items[l.ItemId].LineNo).ToList())).ToList();
    }

    private async Task<Dictionary<Guid, decimal>> CertifiedQuantitiesAsync(Guid contractId, CancellationToken ct)
    {
        var lines = await (from l in db.Set<SubcontractProgress>().AsNoTracking()
                           join c in db.Set<SubcontractCertificate>() on l.CertificateId equals c.Id
                           where c.SubcontractId == contractId && c.Status == SubcontractCertificateStatus.Invoiced
                           select new { l.ItemId, l.QuantityToDate }).ToListAsync(ct);
        return lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Max(x => x.QuantityToDate));
    }

    private async Task<(decimal Retention, decimal RetentionReleased, decimal TaxRetention, decimal TaxRetentionReleased)> HeldAsync(Guid contractId,
        CancellationToken ct)
    {
        var list = await db.Set<SupplierInvoice>().AsNoTracking().Include(i => i.Lines)
            .Where(i => i.SubcontractId == contractId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Reversed).ToListAsync(ct);
        var lines = list.SelectMany(i => i.Lines).ToList();
        return (list.Sum(i => i.RetentionAmount), lines.Where(l => l.LineType == InvoiceLineType.RetentionRelease).Sum(l => l.Amount),
            list.Sum(i => i.TaxRetentionAmount), lines.Where(l => l.LineType == InvoiceLineType.TaxRelease).Sum(l => l.Amount));
    }

    private static string Ref(string? supplierRef, string fallback) => string.IsNullOrWhiteSpace(supplierRef) ? fallback : supplierRef.Trim();

    private async Task<Subcontract> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<Subcontract>().Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id && p.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Subcontract", id);
}

internal static class SubcontractEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing/subcontracts").WithTags("Subcontracts").RequireAuthorization();
        g.MapGet("", async (ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? projectId = null) =>
            await (from c in db.Set<Subcontract>().AsNoTracking()
                   join s in db.Set<Supplier>() on c.SupplierId equals s.Id
                   where c.CompanyId == t.RequireCompanyId() && (projectId == null || c.ProjectId == projectId)
                   orderby c.Code
                   select new
                   {
                       c.Id, c.Code, c.NameEn, c.NameAr, SupplierNameEn = s.NameEn, SupplierNameAr = s.NameAr, c.ProjectId, c.ContractValue, c.RetentionPercent,
                       c.TaxRetentionPercent, c.TaxClearanceReceivedOn, Status = c.Status.ToString()
                   }).ToListAsync(ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/{id:guid}", (Guid id, SubcontractService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("", async (SubcontractRequest r, SubcontractService s, CancellationToken ct) => await s.GetAsync((await s.SaveAsync(null, r, ct)).Id, ct))
            .RequirePermission(PurchasingPermissions.SubcontractsManage);
        g.MapPut("/{id:guid}", async (Guid id, SubcontractRequest r, SubcontractService s, CancellationToken ct) => await s.GetAsync((await s.SaveAsync(id, r, ct)).Id, ct))
            .RequirePermission(PurchasingPermissions.SubcontractsManage);
        g.MapPost("/{id:guid}/items", async (Guid id, List<SubcontractItemRequest> items, SubcontractService s, CancellationToken ct) =>
            await s.GetAsync((await s.AddItemsAsync(id, items, ct)).Id, ct)).RequirePermission(PurchasingPermissions.SubcontractsManage);
        g.MapGet("/{id:guid}/certificates", (Guid id, SubcontractService s, CancellationToken ct) => s.CertificatesAsync(id, null, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/{id:guid}/certificates", async (Guid id, SubcontractCertificateRequest r, SubcontractService s, CancellationToken ct) =>
            (await s.CertificatesAsync(id, (await s.SaveCertificateAsync(id, null, r, ct)).Id, ct)).Single()).RequirePermission(PurchasingPermissions.SubcontractsManage);
        g.MapPut("/{id:guid}/certificates/{certId:guid}", async (Guid id, Guid certId, SubcontractCertificateRequest r, SubcontractService s, CancellationToken ct) =>
            (await s.CertificatesAsync(id, (await s.SaveCertificateAsync(id, certId, r, ct)).Id, ct)).Single()).RequirePermission(PurchasingPermissions.SubcontractsManage);
        g.MapPost("/{id:guid}/certificates/{certId:guid}/invoice", async (Guid id, Guid certId, SubcontractInvoiceRequest r, SubcontractService s, CancellationToken ct) =>
            (await s.CertificatesAsync(id, (await s.InvoiceAsync(id, certId, r, ct)).Id, ct)).Single()).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("/{id:guid}/release-retention", async (Guid id, SubcontractReleaseRequest r, SubcontractService s, CancellationToken ct) =>
            new { SupplierInvoiceId = await s.ReleaseRetentionAsync(id, r, ct) }).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("/{id:guid}/tax-clearance", async (Guid id, TaxClearanceRequest r, SubcontractService s, CancellationToken ct) =>
            new { SupplierInvoiceId = await s.TaxClearanceAsync(id, r, ct) }).RequirePermission(PurchasingPermissions.InvoicesPost);
    }
}

using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Purchasing.Documents;

public enum ApplicationStatus { Active, Reversed }

/// <summary>Part of a supplier credit (a debit note beyond what was open on its invoice) set against another invoice of the supplier.</summary>
public sealed class DebitApplication : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid SupplierId { get; init; }
    public Guid DebitNoteId { get; init; }
    public Guid SupplierInvoiceId { get; init; }
    public DateOnly ApplicationDate { get; init; }
    public decimal Amount { get; init; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Active;
}

/// <summary>Money the supplier paid back for a credit: debit the bank or cash account, credit Accounts Payable.</summary>
public sealed class SupplierRefund : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string RefundNo { get; init; }
    public Guid SupplierId { get; init; }
    public Guid DebitNoteId { get; init; }
    public DateOnly RefundDate { get; init; }
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public string? Reference { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public Guid? JournalEntryId { get; set; }
}

internal sealed class DebitApplicationConfiguration : IEntityTypeConfiguration<DebitApplication>
{
    public void Configure(EntityTypeBuilder<DebitApplication> b) => b.ToTable("debit_application", "pur");
}

internal sealed class SupplierRefundConfiguration : IEntityTypeConfiguration<SupplierRefund>
{
    public void Configure(EntityTypeBuilder<SupplierRefund> b) => b.ToTable("supplier_refund", "pur");
}

public sealed record SupplierCreditDto(Guid DebitNoteId, string DebitNoteNo, DateOnly Date, Guid SupplierId, string SupplierNameEn, string SupplierNameAr,
    decimal Original, decimal Unapplied);

public sealed record ApplyDebitRequest(Guid DebitNoteId, DateOnly Date, IReadOnlyList<PaymentAllocationRequest> Allocations);

public sealed record SupplierRefundRequest(Guid DebitNoteId, DateOnly Date, Guid AccountId, decimal Amount, string? Reference);

public sealed record SupplierRefundDto(Guid Id, string RefundNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, Guid DebitNoteId,
    DateOnly RefundDate, Guid AccountId, decimal Amount, string? Reference, string Status, Guid? JournalEntryId);

/// <summary>Supplier credits from debit notes on paid invoices: set them against other invoices, or record the refund the supplier pays.</summary>
internal sealed class SupplierCreditService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting,
    IFinanceQueries finance)
{
    public const string RefundSourceType = "supplier_refund";

    public Task<List<SupplierCreditDto>> ListAsync(Guid? supplierId, CancellationToken ct) =>
        (from n in db.Set<DebitNote>().AsNoTracking()
         join s in db.Set<Supplier>() on n.SupplierId equals s.Id
         where n.CompanyId == tenant.RequireCompanyId() && n.UnappliedAmount > 0 && n.Status == DocumentStatus.Posted
               && (supplierId == null || n.SupplierId == supplierId)
         orderby n.NoteDate
         select new SupplierCreditDto(n.Id, n.DebitNoteNo, n.NoteDate, s.Id, s.NameEn, s.NameAr, n.Total, n.UnappliedAmount)).ToListAsync(ct);

    public Task ApplyAsync(ApplyDebitRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var note = await NoteAsync(r.DebitNoteId, token);
            if (r.Allocations is not { Count: > 0 } || r.Allocations.Any(a => a.Amount <= 0))
                throw new BusinessRuleException("pur.payment_allocations", "Allocate positive amounts to the invoices.", "وزّع مبالغ موجبة على الفواتير.");
            var total = r.Allocations.Sum(a => Money.Round(a.Amount));
            if (total > note.UnappliedAmount)
                throw new BusinessRuleException("pur.credit_exceeded", $"Only {note.UnappliedAmount:N2} of this credit is left.", $"المتبقي من هذا الرصيد {note.UnappliedAmount:N2} فقط.");
            var ids = r.Allocations.Select(a => a.SupplierInvoiceId).ToList();
            var invoices = await db.Set<SupplierInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            foreach (var a in r.Allocations)
            {
                if (!invoices.TryGetValue(a.SupplierInvoiceId, out var invoice) || invoice.SupplierId != note.SupplierId)
                    throw new NotFoundException("Supplier invoice", a.SupplierInvoiceId);
                var amount = Money.Round(a.Amount);
                if (invoice.Currency != Money.BaseCurrency || invoice.WhtAmount != 0)
                    throw new BusinessRuleException("pur.credit_invoice", "Credits can only be set against QAR invoices without withholding tax.",
                        "يمكن تسوية الأرصدة الدائنة مع فواتير الريال القطري غير الخاضعة للاستقطاع فقط.");
                if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid) || amount > invoice.OpenAmount)
                    throw new BusinessRuleException("pur.overpayment", $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} open.",
                        $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} فقط.");
                invoice.DebitedAmount += amount;
                invoice.DebitedBase += amount;
                SupplierInvoiceService.RefreshPaymentStatus(invoice);
                db.Add(new DebitApplication
                {
                    CompanyId = invoice.CompanyId, SupplierId = note.SupplierId, DebitNoteId = note.Id, SupplierInvoiceId = invoice.Id, ApplicationDate = r.Date,
                    Amount = amount
                });
            }
            note.UnappliedAmount -= total;
            await db.SaveChangesAsync(token);
        }, ct);

    public Task<SupplierRefund> RefundAsync(SupplierRefundRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var note = await NoteAsync(r.DebitNoteId, token);
            var amount = Money.Round(r.Amount);
            if (amount <= 0 || amount > note.UnappliedAmount)
                throw new BusinessRuleException("pur.credit_exceeded", $"Only {note.UnappliedAmount:N2} of this credit is open.", $"المتبقي من هذا الرصيد {note.UnappliedAmount:N2} فقط.");
            var account = await finance.FindPostableAccountAsync(r.AccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("pur.payment_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");
            var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == note.SupplierId, token);
            var refund = new SupplierRefund
            {
                CompanyId = note.CompanyId, RefundNo = await numbering.NextAsync(RefundSourceType, "PREF", r.Date, ct: token), SupplierId = note.SupplierId,
                DebitNoteId = note.Id, RefundDate = r.Date, AccountId = account.Id, Amount = amount, Reference = SupplierService.Clean(r.Reference)
            };
            db.Add(refund);
            note.UnappliedAmount -= amount;
            refund.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = RefundSourceType, SourceId = refund.Id, SourceNo = refund.RefundNo, PostingDate = r.Date,
                MemoEn = $"Refund from {supplier.NameEn}", MemoAr = $"مبلغ مسترد من {supplier.NameAr}",
                Lines =
                [
                    new PostingLine { AccountId = account.Id, Side = PostingSide.Debit, Amount = amount },
                    new PostingLine
                    {
                        AccountRole = AccountRoles.AccountsPayable, Side = PostingSide.Credit, Amount = amount, PartyType = PartyType.Supplier, PartyId = supplier.Id
                    }
                ]
            }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return refund;
        }, ct);

    public Task<SupplierRefund> ReverseRefundAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var refund = await db.Set<SupplierRefund>().SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), token)
                         ?? throw new NotFoundException("Refund", id);
            if (refund.Status != DocumentStatus.Posted)
                throw new ConflictException("pur.not_posted", "This refund is already reversed.", "تم عكس هذا المبلغ المسترد مسبقاً.");
            await posting.ReverseAsync(RefundSourceType, refund.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var note = await db.Set<DebitNote>().SingleAsync(n => n.Id == refund.DebitNoteId, token);
            note.UnappliedAmount += refund.Amount;
            refund.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return refund;
        }, ct);

    /// <summary>Before a debit note is reversed: refunds must be reversed first; its applications come back off the invoices.</summary>
    internal async Task UnwindAsync(Guid debitNoteId, CancellationToken ct)
    {
        if (await db.Set<SupplierRefund>().AnyAsync(x => x.DebitNoteId == debitNoteId && x.Status == DocumentStatus.Posted, ct))
            throw new BusinessRuleException("pur.credit_refunded", "The supplier refunded part of this credit; reverse the refund first.",
                "استرد المورد جزءاً من هذا الرصيد؛ اعكس عملية الاسترداد أولاً.");
        var applications = await db.Set<DebitApplication>().Where(x => x.DebitNoteId == debitNoteId && x.Status == ApplicationStatus.Active).ToListAsync(ct);
        if (applications.Count == 0) return;
        var ids = applications.Select(a => a.SupplierInvoiceId).Distinct().ToList();
        var invoices = await db.Set<SupplierInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var a in applications)
        {
            var invoice = invoices[a.SupplierInvoiceId];
            invoice.DebitedAmount -= a.Amount;
            invoice.DebitedBase -= a.Amount;
            SupplierInvoiceService.RefreshPaymentStatus(invoice);
            a.Status = ApplicationStatus.Reversed;
        }
    }

    private async Task<DebitNote> NoteAsync(Guid id, CancellationToken ct)
    {
        var note = await db.Set<DebitNote>().SingleOrDefaultAsync(n => n.Id == id && n.CompanyId == tenant.RequireCompanyId(), ct)
                   ?? throw new NotFoundException("Debit note", id);
        if (note.Status != DocumentStatus.Posted)
            throw new BusinessRuleException("pur.credit_inactive", "This debit note is reversed.", "تم عكس هذا الإشعار.");
        return note;
    }
}

internal static class SupplierCreditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing").WithTags("Purchasing").RequireAuthorization();
        g.MapGet("/credits", (SupplierCreditService s, CancellationToken ct, Guid? supplierId = null) => s.ListAsync(supplierId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/credits/apply", async (ApplyDebitRequest r, SupplierCreditService s, CancellationToken ct) =>
        {
            await s.ApplyAsync(r, ct);
            return Results.NoContent();
        }).RequirePermission(PurchasingPermissions.PaymentsPost);
        g.MapGet("/refunds", (ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? supplierId = null) => RefundsAsync(db, t, null, supplierId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/refunds", async (SupplierRefundRequest r, SupplierCreditService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await RefundsAsync(db, t, (await s.RefundAsync(r, ct)).Id, null, ct)).Single()).RequirePermission(PurchasingPermissions.PaymentsPost);
        g.MapPost("/refunds/{id:guid}/reverse", async (Guid id, ReverseRequest r, SupplierCreditService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await RefundsAsync(db, t, (await s.ReverseRefundAsync(id, r, ct)).Id, null, ct)).Single()).RequirePermission(PurchasingPermissions.PaymentsPost);
    }

    private static Task<List<SupplierRefundDto>> RefundsAsync(ErpDbContext db, ITenantContext t, Guid? id, Guid? supplierId, CancellationToken ct) =>
        (from x in db.Set<SupplierRefund>().AsNoTracking()
         join s in db.Set<Supplier>() on x.SupplierId equals s.Id
         where x.CompanyId == t.RequireCompanyId() && (id == null || x.Id == id) && (supplierId == null || x.SupplierId == supplierId)
         orderby x.RefundDate descending, x.RefundNo descending
         select new SupplierRefundDto(x.Id, x.RefundNo, x.SupplierId, s.NameEn, s.NameAr, x.DebitNoteId, x.RefundDate, x.AccountId, x.Amount, x.Reference,
             x.Status.ToString(), x.JournalEntryId)).Take(300).ToListAsync(ct);
}

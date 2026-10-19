using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Documents;

public enum CreditSource { Receipt, CreditNote }

public enum ApplicationStatus { Active, Reversed }

/// <summary>Part of a customer credit (receipt on account, or credit note beyond the open invoice) applied to an invoice.</summary>
public sealed class CreditApplication : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid CustomerId { get; init; }
    public CreditSource SourceType { get; init; }
    public Guid SourceId { get; init; }
    public Guid SalesInvoiceId { get; init; }
    public DateOnly ApplicationDate { get; init; }
    public decimal Amount { get; init; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Active;
}

/// <summary>Money paid back to a customer out of a credit: debit Accounts Receivable, credit the bank or cash account.</summary>
public sealed class CustomerRefund : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string RefundNo { get; init; }
    public Guid CustomerId { get; init; }
    public CreditSource SourceType { get; init; }
    public Guid SourceId { get; init; }
    public DateOnly RefundDate { get; init; }
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public string? Reference { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public Guid? JournalEntryId { get; set; }
}

internal sealed class CreditApplicationConfiguration : IEntityTypeConfiguration<CreditApplication>
{
    public void Configure(EntityTypeBuilder<CreditApplication> b) => b.ToTable("credit_application", "sal");
}

internal sealed class CustomerRefundConfiguration : IEntityTypeConfiguration<CustomerRefund>
{
    public void Configure(EntityTypeBuilder<CustomerRefund> b) => b.ToTable("customer_refund", "sal");
}

public sealed record CustomerCreditDto(CreditSource SourceType, Guid SourceId, string DocumentNo, DateOnly Date, Guid CustomerId, string CustomerNameEn,
    string CustomerNameAr, decimal Original, decimal Unapplied);

public sealed record ApplyCreditRequest(CreditSource SourceType, Guid SourceId, DateOnly Date, IReadOnlyList<ReceiptAllocationRequest> Allocations);

public sealed record RefundRequest(CreditSource SourceType, Guid SourceId, DateOnly Date, Guid AccountId, decimal Amount, string? Reference);

public sealed record RefundDto(Guid Id, string RefundNo, Guid CustomerId, string CustomerNameEn, string CustomerNameAr, string SourceType, Guid SourceId,
    DateOnly RefundDate, Guid AccountId, decimal Amount, string? Reference, string Status, Guid? JournalEntryId);

/// <summary>Customer credits: list what is unapplied, apply it to open invoices, or refund it.</summary>
internal sealed class CustomerCreditService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting,
    IFinanceQueries finance)
{
    public const string RefundSourceType = "customer_refund";

    public async Task<List<CustomerCreditDto>> ListAsync(Guid? customerId, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var receipts = await (from r in db.Set<CustomerReceipt>().AsNoTracking()
                              join c in db.Set<Customer>() on r.CustomerId equals c.Id
                              where r.CompanyId == companyId && r.UnappliedAmount > 0 && (customerId == null || r.CustomerId == customerId)
                                    && (r.Status == ReceiptStatus.Posted || r.Status == ReceiptStatus.PdcHeld || r.Status == ReceiptStatus.PdcCleared)
                              select new CustomerCreditDto(CreditSource.Receipt, r.Id, r.ReceiptNo, r.ReceiptDate, c.Id, c.NameEn, c.NameAr, r.Amount,
                                  r.UnappliedAmount)).ToListAsync(ct);
        var notes = await (from n in db.Set<CreditNote>().AsNoTracking()
                           join c in db.Set<Customer>() on n.CustomerId equals c.Id
                           where n.CompanyId == companyId && n.UnappliedAmount > 0 && n.Status == DocumentStatus.Posted
                                 && (customerId == null || n.CustomerId == customerId)
                           select new CustomerCreditDto(CreditSource.CreditNote, n.Id, n.CreditNoteNo, n.NoteDate, c.Id, c.NameEn, c.NameAr, n.Total,
                               n.UnappliedAmount)).ToListAsync(ct);
        return [.. receipts.Concat(notes).OrderBy(x => x.Date).ThenBy(x => x.DocumentNo)];
    }

    public Task ApplyAsync(ApplyCreditRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (customerId, unapplied, consume) = await SourceAsync(r.SourceType, r.SourceId, token);
            if (r.Allocations is not { Count: > 0 } || r.Allocations.Any(a => a.Amount <= 0))
                throw new BusinessRuleException("sal.receipt_allocations", "Allocate positive amounts to the invoices.", "وزّع مبالغ موجبة على الفواتير.");
            var total = r.Allocations.Sum(a => Money.Round(a.Amount));
            if (total > unapplied)
                throw new BusinessRuleException("sal.credit_exceeded", $"Only {unapplied:N2} of this credit is left.", $"المتبقي من هذا الرصيد الدائن {unapplied:N2} فقط.");
            var ids = r.Allocations.Select(a => a.SalesInvoiceId).ToList();
            var invoices = await db.Set<SalesInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            foreach (var a in r.Allocations)
            {
                if (!invoices.TryGetValue(a.SalesInvoiceId, out var invoice) || invoice.CustomerId != customerId)
                    throw new NotFoundException("Sales invoice", a.SalesInvoiceId);
                var amount = Money.Round(a.Amount);
                if (invoice.Currency != Money.BaseCurrency)
                    throw new BusinessRuleException("sal.credit_currency", "Credits can only be applied to QAR invoices.", "يمكن تطبيق الأرصدة الدائنة على فواتير الريال القطري فقط.");
                if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid) || amount > invoice.OpenAmount)
                    throw new BusinessRuleException("sal.overpayment", $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} open.",
                        $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} فقط.");
                if (r.SourceType == CreditSource.Receipt)
                {
                    invoice.PaidAmount += amount;
                    invoice.PaidBase += amount;
                }
                else
                {
                    invoice.CreditedAmount += amount;
                    invoice.CreditedBase += amount;
                }
                SalesInvoiceService.RefreshPaymentStatus(invoice);
                db.Add(new CreditApplication
                {
                    CompanyId = invoice.CompanyId, CustomerId = customerId, SourceType = r.SourceType, SourceId = r.SourceId, SalesInvoiceId = invoice.Id,
                    ApplicationDate = r.Date, Amount = amount
                });
            }
            consume(total);
            await db.SaveChangesAsync(token);
        }, ct);

    public Task<CustomerRefund> RefundAsync(RefundRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (customerId, unapplied, consume) = await SourceAsync(r.SourceType, r.SourceId, token);
            var amount = Money.Round(r.Amount);
            if (amount <= 0 || amount > unapplied)
                throw new BusinessRuleException("sal.credit_exceeded", $"Only {unapplied:N2} of this credit can be refunded.", $"يمكن رد {unapplied:N2} فقط من هذا الرصيد.");
            var account = await finance.FindPostableAccountAsync(r.AccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("sal.deposit_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");
            var customer = await db.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == customerId, token);
            var refund = new CustomerRefund
            {
                CompanyId = customer.CompanyId, RefundNo = await numbering.NextAsync(RefundSourceType, "SREF", r.Date, ct: token), CustomerId = customerId,
                SourceType = r.SourceType, SourceId = r.SourceId, RefundDate = r.Date, AccountId = account.Id, Amount = amount,
                Reference = CustomerService.Clean(r.Reference)
            };
            db.Add(refund);
            consume(amount);
            refund.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = RefundSourceType, SourceId = refund.Id, SourceNo = refund.RefundNo, PostingDate = r.Date,
                MemoEn = $"Refund to {customer.NameEn}", MemoAr = $"رد مبلغ إلى {customer.NameAr}",
                Lines =
                [
                    new PostingLine
                    {
                        AccountRole = AccountRoles.AccountsReceivable, Side = PostingSide.Debit, Amount = amount, PartyType = PartyType.Customer, PartyId = customerId
                    },
                    new PostingLine { AccountId = account.Id, Side = PostingSide.Credit, Amount = amount }
                ]
            }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return refund;
        }, ct);

    public Task<CustomerRefund> ReverseRefundAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var refund = await db.Set<CustomerRefund>().SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), token)
                         ?? throw new NotFoundException("Refund", id);
            if (refund.Status != DocumentStatus.Posted)
                throw new ConflictException("sal.not_posted", "This refund is already reversed.", "تم عكس هذا الرد مسبقاً.");
            await posting.ReverseAsync(RefundSourceType, refund.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var (_, _, consume) = await SourceAsync(refund.SourceType, refund.SourceId, token, allowReversed: true);
            consume(-refund.Amount);
            refund.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return refund;
        }, ct);

    /// <summary>
    /// Called when a receipt or credit note is undone: its later applications are taken back off the invoices. Refunds already paid
    /// out must be reversed first.
    /// </summary>
    internal async Task UnwindAsync(CreditSource source, Guid sourceId, CancellationToken ct)
    {
        if (await db.Set<CustomerRefund>().AnyAsync(x => x.SourceType == source && x.SourceId == sourceId && x.Status == DocumentStatus.Posted, ct))
            throw new BusinessRuleException("sal.credit_refunded", "Part of this credit was refunded; reverse the refund first.",
                "تم رد جزء من هذا الرصيد؛ اعكس عملية الرد أولاً.");
        var applications = await db.Set<CreditApplication>()
            .Where(x => x.SourceType == source && x.SourceId == sourceId && x.Status == ApplicationStatus.Active).ToListAsync(ct);
        if (applications.Count == 0) return;
        var ids = applications.Select(a => a.SalesInvoiceId).Distinct().ToList();
        var invoices = await db.Set<SalesInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var a in applications)
        {
            var invoice = invoices[a.SalesInvoiceId];
            if (source == CreditSource.Receipt)
            {
                invoice.PaidAmount -= a.Amount;
                invoice.PaidBase -= a.Amount;
            }
            else
            {
                invoice.CreditedAmount -= a.Amount;
                invoice.CreditedBase -= a.Amount;
            }
            SalesInvoiceService.RefreshPaymentStatus(invoice);
            a.Status = ApplicationStatus.Reversed;
        }
    }

    private async Task<(Guid CustomerId, decimal Unapplied, Action<decimal> Consume)> SourceAsync(CreditSource source, Guid id, CancellationToken ct,
        bool allowReversed = false)
    {
        var companyId = tenant.RequireCompanyId();
        if (source == CreditSource.Receipt)
        {
            var receipt = await db.Set<CustomerReceipt>().SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct)
                          ?? throw new NotFoundException("Customer receipt", id);
            if (!allowReversed && receipt.Status is ReceiptStatus.Bounced or ReceiptStatus.Reversed)
                throw new BusinessRuleException("sal.credit_inactive", "This receipt is no longer valid.", "هذا السند لم يعد سارياً.");
            return (receipt.CustomerId, receipt.UnappliedAmount, x => receipt.UnappliedAmount -= x);
        }
        var note = await db.Set<CreditNote>().SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct)
                   ?? throw new NotFoundException("Credit note", id);
        if (!allowReversed && note.Status != DocumentStatus.Posted)
            throw new BusinessRuleException("sal.credit_inactive", "This credit note is reversed.", "تم عكس هذا الإشعار.");
        return (note.CustomerId, note.UnappliedAmount, x => note.UnappliedAmount -= x);
    }
}

internal static class CustomerCreditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales").WithTags("Sales").RequireAuthorization();
        g.MapGet("/credits", (CustomerCreditService s, CancellationToken ct, Guid? customerId = null) => s.ListAsync(customerId, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/credits/apply", async (ApplyCreditRequest r, CustomerCreditService s, CancellationToken ct) =>
        {
            await s.ApplyAsync(r, ct);
            return Results.NoContent();
        }).RequirePermission(SalesPermissions.ReceiptsPost);
        g.MapGet("/refunds", (ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? customerId = null) => RefundsAsync(db, t, null, customerId, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/refunds", async (RefundRequest r, CustomerCreditService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await RefundsAsync(db, t, (await s.RefundAsync(r, ct)).Id, null, ct)).Single()).RequirePermission(SalesPermissions.ReceiptsPost);
        g.MapPost("/refunds/{id:guid}/reverse", async (Guid id, ReverseRequest r, CustomerCreditService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await RefundsAsync(db, t, (await s.ReverseRefundAsync(id, r, ct)).Id, null, ct)).Single()).RequirePermission(SalesPermissions.ReceiptsPost);
    }

    private static Task<List<RefundDto>> RefundsAsync(ErpDbContext db, ITenantContext t, Guid? id, Guid? customerId, CancellationToken ct) =>
        (from x in db.Set<CustomerRefund>().AsNoTracking()
         join c in db.Set<Customer>() on x.CustomerId equals c.Id
         where x.CompanyId == t.RequireCompanyId() && (id == null || x.Id == id) && (customerId == null || x.CustomerId == customerId)
         orderby x.RefundDate descending, x.RefundNo descending
         select new RefundDto(x.Id, x.RefundNo, x.CustomerId, c.NameEn, c.NameAr, x.SourceType.ToString(), x.SourceId, x.RefundDate, x.AccountId, x.Amount,
             x.Reference, x.Status.ToString(), x.JournalEntryId)).Take(300).ToListAsync(ct);
}

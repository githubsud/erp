using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

public sealed record OpeningInvoiceRequest(Guid CustomerId, string Reference, DateOnly InvoiceDate, DateOnly? DueDate, decimal Amount);

public sealed record CustomerOpeningRequest(DateOnly PostingDate, IReadOnlyList<OpeningInvoiceRequest> Invoices);

/// <summary>
/// Customers' unpaid invoices from the previous system, one document each so they age, appear on statements and can be
/// collected like any invoice. Posted on the cut-over date: debit Accounts Receivable, credit Opening Balance Equity.
/// They use their own number series so the sales invoice series stays gapless.
/// </summary>
internal sealed class CustomerOpeningService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting)
{
    public Task<List<SalesInvoice>> CreateAsync(CustomerOpeningRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Invoices is not { Count: > 0 })
                throw new BusinessRuleException("sal.opening_empty", "Enter at least one open invoice.", "أدخل فاتورة مفتوحة واحدة على الأقل.");
            var ids = r.Invoices.Select(i => i.CustomerId).Distinct().ToList();
            var customers = await db.Set<Customer>().AsNoTracking().Where(c => ids.Contains(c.Id) && c.CompanyId == tenant.RequireCompanyId())
                .ToDictionaryAsync(c => c.Id, token);
            var created = new List<SalesInvoice>();
            foreach (var line in r.Invoices)
            {
                if (!customers.TryGetValue(line.CustomerId, out var customer)) throw new NotFoundException("Customer", line.CustomerId);
                var amount = Money.Round(line.Amount);
                if (amount <= 0)
                    throw new BusinessRuleException("sal.opening_amount", "Opening invoice amounts must be positive.", "يجب أن تكون مبالغ الفواتير الافتتاحية موجبة.");
                if (line.InvoiceDate > r.PostingDate)
                    throw new BusinessRuleException("sal.opening_date", "Opening invoices must be dated on or before the cut-over date.",
                        "يجب أن يكون تاريخ الفاتورة الافتتاحية في تاريخ التحويل أو قبله.");
                var reference = Guard.Required(line.Reference, "reference");
                var invoice = new SalesInvoice
                {
                    CompanyId = customer.CompanyId,
                    InvoiceNo = await numbering.NextAsync("customer_opening", "OBC", r.PostingDate, ct: token),
                    CustomerId = customer.Id,
                    InvoiceDate = line.InvoiceDate,
                    DueDate = line.DueDate ?? line.InvoiceDate.AddDays(customer.PaymentTermsDays),
                    CustomerRef = reference,
                    IsOpening = true,
                    Total = amount,
                    TotalBase = amount,
                    Status = InvoiceStatus.Posted,
                    Notes = "Opening balance · رصيد افتتاحي"
                };
                invoice.Lines.Add(new SalesInvoiceLine
                {
                    SalesInvoiceId = invoice.Id, LineNo = 1, LineType = InvoiceLineType.Service, Description = $"Opening balance {reference}",
                    Quantity = 1, UnitPrice = amount, Amount = amount, UomQuantity = 1, UomPrice = amount
                });
                db.Add(invoice);
                invoice.JournalEntryId = (await posting.PostAsync(new PostingRequest
                {
                    SourceType = SalesInvoiceService.SourceType, SourceId = invoice.Id, SourceNo = invoice.InvoiceNo, PostingDate = r.PostingDate,
                    MemoEn = $"Opening balance {reference} — {customer.NameEn}", MemoAr = $"رصيد افتتاحي {reference} — {customer.NameAr}",
                    Lines =
                    [
                        new PostingLine
                        {
                            AccountRole = AccountRoles.AccountsReceivable, Side = PostingSide.Debit, Amount = amount, PartyType = PartyType.Customer,
                            PartyId = customer.Id
                        },
                        new PostingLine { AccountRole = AccountRoles.OpeningBalanceEquity, Side = PostingSide.Credit, Amount = amount }
                    ]
                }, token)).JournalEntryId;
                created.Add(invoice);
            }
            await db.SaveChangesAsync(token);
            return created;
        }, ct);
}

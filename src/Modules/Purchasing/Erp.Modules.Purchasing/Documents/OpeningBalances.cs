using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Documents;

public sealed record OpeningInvoiceRequest(Guid SupplierId, string Reference, DateOnly InvoiceDate, DateOnly? DueDate, decimal Amount);

public sealed record SupplierOpeningRequest(DateOnly PostingDate, IReadOnlyList<OpeningInvoiceRequest> Invoices);

/// <summary>
/// Suppliers' unpaid invoices from the previous system, one document each so they age and can be paid like any invoice.
/// Posted on the cut-over date: debit Opening Balance Equity, credit Accounts Payable. No withholding tax is computed on them.
/// </summary>
internal sealed class SupplierOpeningService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting)
{
    public Task<List<SupplierInvoice>> CreateAsync(SupplierOpeningRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Invoices is not { Count: > 0 })
                throw new BusinessRuleException("pur.opening_empty", "Enter at least one open invoice.", "أدخل فاتورة مفتوحة واحدة على الأقل.");
            var ids = r.Invoices.Select(i => i.SupplierId).Distinct().ToList();
            var suppliers = await db.Set<Supplier>().AsNoTracking().Where(s => ids.Contains(s.Id) && s.CompanyId == tenant.RequireCompanyId())
                .ToDictionaryAsync(s => s.Id, token);
            var created = new List<SupplierInvoice>();
            foreach (var line in r.Invoices)
            {
                if (!suppliers.TryGetValue(line.SupplierId, out var supplier)) throw new NotFoundException("Supplier", line.SupplierId);
                var amount = Money.Round(line.Amount);
                if (amount <= 0)
                    throw new BusinessRuleException("pur.opening_amount", "Opening invoice amounts must be positive.", "يجب أن تكون مبالغ الفواتير الافتتاحية موجبة.");
                if (line.InvoiceDate > r.PostingDate)
                    throw new BusinessRuleException("pur.opening_date", "Opening invoices must be dated on or before the cut-over date.",
                        "يجب أن يكون تاريخ الفاتورة الافتتاحية في تاريخ التحويل أو قبله.");
                var reference = Guard.Required(line.Reference, "reference");
                var invoice = new SupplierInvoice
                {
                    CompanyId = supplier.CompanyId,
                    InvoiceNo = await numbering.NextAsync("supplier_opening", "OBS", r.PostingDate, ct: token),
                    SupplierId = supplier.Id,
                    SupplierRef = reference,
                    InvoiceDate = line.InvoiceDate,
                    DueDate = line.DueDate ?? line.InvoiceDate.AddDays(supplier.PaymentTermsDays),
                    IsOpening = true,
                    Total = amount,
                    TotalBase = amount,
                    Status = InvoiceStatus.Posted,
                    MatchStatus = MatchStatus.NotApplicable,
                    Notes = "Opening balance · رصيد افتتاحي"
                };
                invoice.Lines.Add(new SupplierInvoiceLine
                {
                    SupplierInvoiceId = invoice.Id, LineNo = 1, LineType = InvoiceLineType.Service, Description = $"Opening balance {reference}",
                    Quantity = 1, UnitPrice = amount, Amount = amount
                });
                db.Add(invoice);
                invoice.JournalEntryId = (await posting.PostAsync(new PostingRequest
                {
                    SourceType = SupplierInvoiceService.SourceType, SourceId = invoice.Id, SourceNo = invoice.InvoiceNo, PostingDate = r.PostingDate,
                    MemoEn = $"Opening balance {reference} — {supplier.NameEn}", MemoAr = $"رصيد افتتاحي {reference} — {supplier.NameAr}",
                    Lines =
                    [
                        new PostingLine { AccountRole = AccountRoles.OpeningBalanceEquity, Side = PostingSide.Debit, Amount = amount },
                        new PostingLine
                        {
                            AccountRole = AccountRoles.AccountsPayable, Side = PostingSide.Credit, Amount = amount, PartyType = PartyType.Supplier,
                            PartyId = supplier.Id
                        }
                    ]
                }, token)).JournalEntryId;
                created.Add(invoice);
            }
            await db.SaveChangesAsync(token);
            return created;
        }, ct);
}

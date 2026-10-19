using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Documents;

public sealed record DebitNoteLineRequest(DebitNoteLineType LineType, Guid? SupplierInvoiceLineId, string? Description, decimal Quantity, decimal? UnitPrice = null);

public sealed record DebitNoteRequest(Guid SupplierInvoiceId, DateOnly Date, string Reason, Guid? WarehouseId, IReadOnlyList<DebitNoteLineRequest> Lines);

/// <summary>
/// Debit notes against a posted supplier invoice, debiting Accounts Payable. Returned goods leave stock at weighted-average cost
/// (credit Inventory) and any difference to the invoice price goes to cost of sales, so stock value keeps matching the Inventory
/// account. Allowances on goods revalue the stock still on hand (the part for goods already sold goes to cost of sales); allowances
/// on services or without a line reduce purchase expense. A debit note takes at most what is still open on the invoice.
/// </summary>
internal sealed class DebitNoteService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IStockService stock, IPostingService posting, SupplierCreditService credits)
{
    public const string SourceType = "debit_note";

    public Task<DebitNote> CreateAsync(DebitNoteRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await db.Set<SupplierInvoice>().Include(i => i.Lines)
                              .SingleOrDefaultAsync(i => i.Id == r.SupplierInvoiceId && i.CompanyId == tenant.RequireCompanyId(), token)
                          ?? throw new NotFoundException("Supplier invoice", r.SupplierInvoiceId);
            if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid))
                throw new BusinessRuleException("pur.invoice_not_open", $"Invoice {invoice.InvoiceNo} cannot take a debit note.",
                    $"لا يمكن إصدار إشعار مدين على الفاتورة {invoice.InvoiceNo}.");
            // Withholding tax is withheld on payment, so a note can still reduce it while nothing has been paid.
            if (invoice.WhtAmount != 0 && invoice.WhtWithheld != 0)
                throw new BusinessRuleException("pur.debit_note_wht", "Tax was already withheld on payments of this invoice; reverse those payments first.",
                    "تم استقطاع ضريبة على دفعات هذه الفاتورة؛ اعكس تلك الدفعات أولاً.");
            if (r.Lines is not { Count: > 0 })
                throw new BusinessRuleException("pur.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");

            var grnLineIds = invoice.Lines.Where(l => l.GoodsReceiptLineId is not null).Select(l => l.GoodsReceiptLineId!.Value).ToList();
            var receiptWarehouse = await (from l in db.Set<GoodsReceiptLine>()
                                          join g in db.Set<GoodsReceipt>() on l.GoodsReceiptId equals g.Id
                                          where grnLineIds.Contains(l.Id)
                                          select (Guid?)g.WarehouseId).FirstOrDefaultAsync(token);
            var items = await inventory.GetItemsAsync(invoice.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), token);
            var note = new DebitNote
            {
                CompanyId = invoice.CompanyId,
                DebitNoteNo = await numbering.NextAsync(SourceType, "PDN", r.Date, ct: token),
                SupplierId = invoice.SupplierId,
                SupplierInvoiceId = invoice.Id,
                NoteDate = r.Date,
                WarehouseId = r.WarehouseId ?? receiptWarehouse,
                Reason = Guard.Required(r.Reason, "reason")
            };

            var builder = new PostingBuilder();
            var returns = new List<DebitNoteLine>();
            var revaluations = new List<RevaluationLine>();
            var lineNo = 0;
            foreach (var l in r.Lines)
            {
                var original = l.SupplierInvoiceLineId is { } lineId
                    ? invoice.Lines.SingleOrDefault(x => x.Id == lineId) ?? throw new NotFoundException("Invoice line", lineId)
                    : null;
                decimal price;
                if (l.LineType == DebitNoteLineType.Return)
                {
                    if (original is not { LineType: InvoiceLineType.Goods })
                        throw new BusinessRuleException("pur.return_line", "Returns must refer to a goods line of the invoice.", "يجب ربط المرتجع بسطر بضاعة في الفاتورة.");
                    if (l.Quantity <= 0 || original.QuantityReturned + l.Quantity > original.Quantity)
                        throw new BusinessRuleException("pur.over_return", $"Only {original.Quantity - original.QuantityReturned:0.##} can still be returned.",
                            $"الكمية القابلة للإرجاع {original.Quantity - original.QuantityReturned:0.##} فقط.");
                    original.QuantityReturned += l.Quantity;
                    price = l.UnitPrice ?? original.UnitPrice;
                }
                else
                {
                    if (original is null && string.IsNullOrWhiteSpace(l.Description))
                        throw new BusinessRuleException("pur.line_description", "Describe the allowance.", "أدخل وصف الخصم.");
                    price = l.UnitPrice ?? throw new BusinessRuleException("pur.price_missing", "Enter the allowance amount.", "أدخل مبلغ الخصم.");
                }
                if (l.Quantity <= 0 || price < 0)
                    throw new BusinessRuleException("pur.line_values", "Quantities must be positive and prices not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");

                var line = new DebitNoteLine
                {
                    DebitNoteId = note.Id, LineNo = ++lineNo, LineType = l.LineType, SupplierInvoiceLineId = original?.Id, ItemId = original?.ItemId,
                    Description = SupplierService.Clean(l.Description) ?? original?.Description, Quantity = l.Quantity, UnitPrice = price,
                    Amount = Money.Round(l.Quantity * price)
                };
                note.Lines.Add(line);
                var lineBase = PurchasingRules.ToBase(line.Amount, invoice.ExchangeRate);
                if (l.LineType == DebitNoteLineType.Return) returns.Add(line);
                else if (original is { LineType: InvoiceLineType.Goods, ItemId: { } goodsItem })
                    revaluations.Add(new RevaluationLine(goodsItem, -lineBase, original.Quantity, line.Id));
                else
                    builder.Add(AccountRoles.PurchaseExpense, original?.ItemId is { } svc ? items[svc].ItemGroupId : null, -lineBase);
            }
            note.Total = note.Lines.Sum(x => x.Amount);
            var debitedBefore = await db.Set<DebitNote>().Where(n => n.SupplierInvoiceId == invoice.Id && n.Status == DocumentStatus.Posted)
                .SumAsync(n => (decimal?)n.Total, token) ?? 0;
            if (debitedBefore + note.Total > invoice.Total)
                throw new BusinessRuleException("pur.debit_exceeds_invoice", $"Invoice {invoice.InvoiceNo} totals {invoice.Total:N2} and {debitedBefore:N2} is already credited.",
                    $"إجمالي الفاتورة {invoice.InvoiceNo} هو {invoice.Total:N2} وتم إشعار {debitedBefore:N2} منها مسبقاً.");
            note.AppliedAmount = Math.Min(note.Total, invoice.OpenAmount);
            note.UnappliedAmount = note.Total - note.AppliedAmount;
            if (note.UnappliedAmount > 0 && (invoice.Currency != Money.BaseCurrency || invoice.WhtAmount != 0))
                throw new BusinessRuleException("pur.debit_exceeds_open", $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} open.",
                    $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} فقط.");
            if (invoice.WhtAmount != 0)
            {
                if (note.Lines.Any(x => x.SupplierInvoiceLineId is null))
                    throw new BusinessRuleException("pur.debit_note_wht_line", "On an invoice with withholding tax, each line must refer to an invoice line.",
                        "في الفاتورة الخاضعة للاستقطاع يجب ربط كل سطر بسطر من الفاتورة.");
                note.WhtReduction = Math.Min(invoice.WhtAmount,
                    note.Lines.Sum(x => Money.Round(x.Amount * invoice.Lines.Single(o => o.Id == x.SupplierInvoiceLineId).WhtRate / 100m)));
            }
            var linesBase = note.Lines.Sum(x => PurchasingRules.ToBase(x.Amount, invoice.ExchangeRate));
            note.AppliedBase = PurchasingRules.BaseCleared(note.AppliedAmount, invoice.OpenAmount, invoice.OpenBase, invoice.ExchangeRate);
            note.TotalBase = note.AppliedBase + note.UnappliedAmount;
            // Closing a foreign-currency invoice can leave a cent of conversion rounding; it is an exchange difference.
            builder.Add(AccountRoles.ExchangeDifference, null, linesBase - note.TotalBase);
            db.Add(note);

            if (returns.Count > 0)
            {
                if (note.WarehouseId is not { } warehouseId)
                    throw new BusinessRuleException("pur.return_warehouse", "Choose the warehouse the goods leave from.", "اختر المستودع الذي تخرج منه البضاعة.");
                var results = await stock.PostAsync(new StockPosting(SourceType, note.Id, note.DebitNoteNo, r.Date,
                    returns.Select(x => new StockLine(x.ItemId!.Value, warehouseId, -x.Quantity, SourceLineId: x.Id)).ToList(), IssueExpired: true), token);
                foreach (var x in results)
                {
                    var line = returns.Single(rl => rl.Id == x.SourceLineId);
                    line.StockValue = -x.Value;
                    builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.Value);
                    // What the supplier credits us beyond the stock's average cost (or short of it) is a cost of sales difference.
                    builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, -(PurchasingRules.ToBase(line.Amount, invoice.ExchangeRate) + x.Value));
                }
            }
            foreach (var x in await stock.RevalueAsync(SourceType, note.Id, note.DebitNoteNo, r.Date, revaluations, token))
            {
                builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.ToInventory);
                builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, x.ToCostOfSales);
            }
            builder.Add(AccountRoles.AccountsPayable, null, note.TotalBase, invoice.SupplierId);

            var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == invoice.SupplierId, token);
            note.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = note.Id, SourceNo = note.DebitNoteNo, PostingDate = r.Date,
                MemoEn = $"Debit note {note.DebitNoteNo} on {invoice.SupplierRef} — {supplier.NameEn}",
                MemoAr = $"إشعار مدين {note.DebitNoteNo} على {invoice.SupplierRef} — {supplier.NameAr}",
                Lines = builder.Lines()
            }, token)).JournalEntryId;
            invoice.DebitedAmount += note.AppliedAmount;
            invoice.DebitedBase += note.AppliedBase;
            invoice.WhtAmount -= note.WhtReduction;
            SupplierInvoiceService.RefreshPaymentStatus(invoice);
            await db.SaveChangesAsync(token);
            return note;
        }, ct);

    public Task<DebitNote> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var note = await db.Set<DebitNote>().Include(n => n.Lines)
                           .SingleOrDefaultAsync(n => n.Id == id && n.CompanyId == tenant.RequireCompanyId(), token)
                       ?? throw new NotFoundException("Debit note", id);
            if (note.Status != DocumentStatus.Posted)
                throw new ConflictException("pur.not_posted", "This debit note is already reversed.", "تم عكس هذا الإشعار مسبقاً.");
            await credits.UnwindAsync(note.Id, token);
            await stock.ReverseAsync(SourceType, note.Id, r.Date, token);
            await posting.ReverseAsync(SourceType, note.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var invoice = await db.Set<SupplierInvoice>().Include(i => i.Lines).SingleAsync(i => i.Id == note.SupplierInvoiceId, token);
            if (note.WhtReduction != 0 && invoice.WhtWithheld != 0)
                throw new BusinessRuleException("pur.debit_note_wht", "Tax was withheld on payments since this note; reverse those payments first.",
                    "تم استقطاع ضريبة على دفعات بعد هذا الإشعار؛ اعكس تلك الدفعات أولاً.");
            foreach (var l in note.Lines.Where(l => l.LineType == DebitNoteLineType.Return))
                invoice.Lines.Single(x => x.Id == l.SupplierInvoiceLineId).QuantityReturned -= l.Quantity;
            invoice.DebitedAmount -= note.AppliedAmount;
            invoice.DebitedBase -= note.AppliedBase;
            invoice.WhtAmount += note.WhtReduction;
            SupplierInvoiceService.RefreshPaymentStatus(invoice);
            note.UnappliedAmount = 0;
            note.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return note;
        }, ct);
}

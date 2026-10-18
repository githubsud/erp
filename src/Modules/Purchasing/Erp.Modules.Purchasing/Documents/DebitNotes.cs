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
    IStockService stock, IPostingService posting)
{
    public const string SourceType = "debit_note";

    public Task<DebitNote> CreateAsync(DebitNoteRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await db.Set<SupplierInvoice>().Include(i => i.Lines)
                              .SingleOrDefaultAsync(i => i.Id == r.SupplierInvoiceId && i.CompanyId == tenant.RequireCompanyId(), token)
                          ?? throw new NotFoundException("Supplier invoice", r.SupplierInvoiceId);
            if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid))
                throw new BusinessRuleException("pur.invoice_not_open", $"Invoice {invoice.InvoiceNo} has nothing open to reduce.",
                    $"لا يوجد رصيد مفتوح على الفاتورة {invoice.InvoiceNo}.");
            if (invoice.WhtAmount != 0)
                throw new BusinessRuleException("pur.debit_note_wht", "Invoices with withholding tax cannot take debit notes yet; reverse the invoice instead.",
                    "لا يمكن إصدار إشعار مدين على فاتورة عليها ضريبة استقطاع حالياً؛ اعكس الفاتورة بدلاً من ذلك.");
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
            var linesBase = note.Lines.Sum(x => PurchasingRules.ToBase(x.Amount, invoice.ExchangeRate));
            note.TotalBase = PurchasingRules.BaseCleared(note.Total, invoice.OpenAmount, invoice.OpenBase, invoice.ExchangeRate);
            // Closing a foreign-currency invoice can leave a cent of conversion rounding; it is an exchange difference.
            builder.Add(AccountRoles.ExchangeDifference, null, linesBase - note.TotalBase);
            if (note.Total > invoice.OpenAmount)
                throw new BusinessRuleException("pur.debit_exceeds_open",
                    $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} open; refunds of paid amounts are not supported yet.",
                    $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} فقط؛ استرداد المبالغ المدفوعة غير مدعوم بعد.");
            db.Add(note);

            if (returns.Count > 0)
            {
                if (note.WarehouseId is not { } warehouseId)
                    throw new BusinessRuleException("pur.return_warehouse", "Choose the warehouse the goods leave from.", "اختر المستودع الذي تخرج منه البضاعة.");
                var results = await stock.PostAsync(new StockPosting(SourceType, note.Id, note.DebitNoteNo, r.Date,
                    returns.Select(x => new StockLine(x.ItemId!.Value, warehouseId, -x.Quantity, SourceLineId: x.Id)).ToList()), token);
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
            invoice.DebitedAmount += note.Total;
            invoice.DebitedBase += note.TotalBase;
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
            await stock.ReverseAsync(SourceType, note.Id, r.Date, token);
            await posting.ReverseAsync(SourceType, note.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var invoice = await db.Set<SupplierInvoice>().Include(i => i.Lines).SingleAsync(i => i.Id == note.SupplierInvoiceId, token);
            foreach (var l in note.Lines.Where(l => l.LineType == DebitNoteLineType.Return))
                invoice.Lines.Single(x => x.Id == l.SupplierInvoiceLineId).QuantityReturned -= l.Quantity;
            invoice.DebitedAmount -= note.Total;
            invoice.DebitedBase -= note.TotalBase;
            SupplierInvoiceService.RefreshPaymentStatus(invoice);
            note.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return note;
        }, ct);
}

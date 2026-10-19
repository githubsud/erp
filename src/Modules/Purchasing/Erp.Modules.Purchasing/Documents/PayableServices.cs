using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Documents;

public sealed record InvoiceLineRequest(InvoiceLineType LineType, Guid? GoodsReceiptLineId, Guid? PurchaseOrderLineId, Guid? ItemId, string? Description,
    decimal Quantity, decimal UnitPrice, WhtCategory? WhtCategory = null);

public sealed record InvoiceRequest(Guid SupplierId, string SupplierRef, DateOnly InvoiceDate, DateOnly? DueDate, Guid? PurchaseOrderId, string? Notes,
    IReadOnlyList<InvoiceLineRequest> Lines, string? Currency = null, decimal? ExchangeRate = null);

public sealed record ChargeRequest(ChargeType ChargeType, string? Description, decimal Amount, Guid? SupplierInvoiceId);

public sealed record LandedCostRequest(DateOnly Date, AllocationMethod Method, IReadOnlyList<Guid> GoodsReceiptIds, string? Notes,
    IReadOnlyList<ChargeRequest> Charges);

public sealed record PaymentAllocationRequest(Guid SupplierInvoiceId, decimal Amount);

/// <summary>Allocations are in the invoices' currency; a rate for the payment date is needed when that is not QAR.</summary>
/// <summary>A cheque dated after <see cref="Date"/> is post-dated: it settles the invoices now and leaves the bank when it clears.</summary>
public sealed record PaymentRequest(Guid SupplierId, DateOnly Date, Guid PaymentAccountId, string? Reference, IReadOnlyList<PaymentAllocationRequest> Allocations,
    decimal? ExchangeRate = null, string? ChequeNo = null, DateOnly? ChequeDate = null);

public sealed record PdcClearRequest(DateOnly Date);

/// <summary>Collects signed amounts per account role and key, then emits balanced debit/credit posting lines.</summary>
internal sealed class PostingBuilder
{
    private readonly Dictionary<(string Role, Guid? Key, Guid? Party), decimal> _amounts = [];

    public void Add(string role, Guid? key, decimal signedDebit, Guid? supplierId = null)
    {
        if (signedDebit == 0) return;
        _amounts[(role, key, supplierId)] = _amounts.GetValueOrDefault((role, key, supplierId)) + signedDebit;
    }

    public List<PostingLine> Lines() => _amounts.Where(x => x.Value != 0).Select(x => new PostingLine
    {
        AccountRole = x.Key.Role,
        RoleKey = x.Key.Key,
        Side = x.Value > 0 ? PostingSide.Debit : PostingSide.Credit,
        Amount = Math.Abs(x.Value),
        PartyType = x.Key.Party is null ? null : PartyType.Supplier,
        PartyId = x.Key.Party
    }).ToList();
}

/// <summary>
/// Supplier invoices. Goods lines are matched to receipt lines (3-way: order price → receipt cost → invoice price); a price
/// above the tolerance holds the invoice until approved. Posting clears GRNI at receipt cost; the price difference revalues
/// the stock still on hand and the rest goes to cost of sales. Service lines go to expense, freight/customs lines to the
/// landed cost clearing account. Withholding tax is computed per line now and withheld when paying.
/// </summary>
internal sealed class SupplierInvoiceService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IStockService stock, IPostingService posting)
{
    public const string SourceType = "supplier_invoice";

    public Task<SupplierInvoice> CreateAsync(InvoiceRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var supplier = await PurchaseOrderService.ActiveSupplierAsync(db, tenant, r.SupplierId, token);
            var settings = await db.Set<PurchaseSettings>().AsNoTracking().SingleAsync(s => s.CompanyId == supplier.CompanyId, token);
            if (r.Lines is not { Count: > 0 })
                throw new BusinessRuleException("pur.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");

            var grnLineIds = r.Lines.Where(l => l.GoodsReceiptLineId is not null).Select(l => l.GoodsReceiptLineId!.Value).ToList();
            var grnLines = await (from l in db.Set<GoodsReceiptLine>()
                                  join g in db.Set<GoodsReceipt>() on l.GoodsReceiptId equals g.Id
                                  where grnLineIds.Contains(l.Id) && g.CompanyId == supplier.CompanyId
                                  join o in db.Set<PurchaseOrder>() on g.PurchaseOrderId equals o.Id
                                  select new { Line = l, g.SupplierId, g.Status, o.Currency }).ToDictionaryAsync(x => x.Line.Id, token);
            var poLineIds = r.Lines.Where(l => l.PurchaseOrderLineId is not null).Select(l => l.PurchaseOrderLineId!.Value).ToList();
            var poLines = await (from l in db.Set<PurchaseOrderLine>()
                                 join o in db.Set<PurchaseOrder>() on l.PurchaseOrderId equals o.Id
                                 where poLineIds.Contains(l.Id) && o.CompanyId == supplier.CompanyId
                                 select new { Line = l, o.SupplierId }).ToDictionaryAsync(x => x.Line.Id, token);
            var items = await inventory.GetItemsAsync(r.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value)
                .Concat(grnLines.Values.Select(g => g.Line.ItemId)).Concat(poLines.Values.Select(p => p.Line.ItemId)), token);

            var currency = PurchasingRules.CurrencyCode(r.Currency ?? supplier.Currency);
            var fx = PurchasingRules.ExchangeRate(currency, r.ExchangeRate);
            var invoice = new SupplierInvoice
            {
                CompanyId = supplier.CompanyId,
                Currency = currency,
                ExchangeRate = fx,
                InvoiceNo = await numbering.NextAsync(SourceType, "PINV", r.InvoiceDate, ct: token),
                SupplierId = supplier.Id,
                SupplierRef = Guard.Required(r.SupplierRef, "supplierRef"),
                InvoiceDate = r.InvoiceDate,
                DueDate = r.DueDate ?? r.InvoiceDate.AddDays(supplier.PaymentTermsDays),
                PurchaseOrderId = r.PurchaseOrderId,
                Notes = r.Notes
            };
            var lineNo = 0;
            var variance = false;
            foreach (var l in r.Lines)
            {
                if (l.Quantity <= 0 || l.UnitPrice < 0)
                    throw new BusinessRuleException("pur.line_values", "Quantities must be positive and prices not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");
                var amount = Money.Round(l.Quantity * l.UnitPrice);
                Guid? itemId = l.ItemId;
                Guid? poLineId = l.PurchaseOrderLineId;
                decimal? receiptCost = null;
                decimal lineVariance = 0;
                var poWht = WhtCategory.None;

                switch (l.LineType)
                {
                    case InvoiceLineType.Goods:
                        if (l.GoodsReceiptLineId is not { } grnLineId || !grnLines.TryGetValue(grnLineId, out var grn))
                            throw new BusinessRuleException("pur.goods_needs_receipt", "Goods lines must refer to a goods receipt line.",
                                "يجب ربط بنود البضاعة بسطر استلام.");
                        if (grn.SupplierId != supplier.Id || grn.Status != DocumentStatus.Posted)
                            throw new BusinessRuleException("pur.receipt_mismatch", "The receipt belongs to another supplier or was reversed.",
                                "الاستلام يخص مورداً آخر أو تم عكسه.");
                        if (grn.Currency != currency)
                            throw new BusinessRuleException("pur.currency_mismatch", $"The goods were ordered in {grn.Currency}; invoice them in the same currency.",
                                $"تم طلب البضاعة بعملة {grn.Currency}؛ أصدر الفاتورة بنفس العملة.");
                        if (l.Quantity > grn.Line.Quantity - grn.Line.QuantityInvoiced)
                            throw new BusinessRuleException("pur.over_invoice",
                                $"Only {grn.Line.Quantity - grn.Line.QuantityInvoiced:0.##} received and not yet invoiced.",
                                $"المستلم وغير المفوتر {grn.Line.Quantity - grn.Line.QuantityInvoiced:0.##} فقط.");
                        itemId = grn.Line.ItemId;
                        poLineId = grn.Line.PurchaseOrderLineId;
                        receiptCost = grn.Line.UnitCost;
                        // Matching is in QAR: the invoice price at the invoice rate against the receipt cost.
                        lineVariance = PurchasingRules.ToBase(amount, fx) - Money.Round(l.Quantity * grn.Line.UnitCost);
                        if (!PurchasingRules.WithinTolerance(l.UnitPrice * fx, grn.Line.UnitCost, settings.PriceTolerancePercent)) variance = true;
                        break;
                    case InvoiceLineType.Service:
                        if (poLineId is { } pl)
                        {
                            if (!poLines.TryGetValue(pl, out var po) || po.SupplierId != supplier.Id) throw new NotFoundException("Order line", pl);
                            if (l.Quantity > po.Line.Quantity - po.Line.QuantityInvoiced)
                                throw new BusinessRuleException("pur.over_invoice", "More than ordered would be invoiced.", "الكمية المفوترة تتجاوز المطلوبة.");
                            itemId ??= po.Line.ItemId;
                            poWht = po.Line.WhtCategory;
                        }
                        if (itemId is { } serviceItem && (!items.TryGetValue(serviceItem, out var s) || s.Type != ItemType.Service))
                            throw new BusinessRuleException("pur.service_item", "Service lines need a service item (or just a description).",
                                "تتطلب بنود الخدمات صنفاً خدمياً أو وصفاً.");
                        if (itemId is null && string.IsNullOrWhiteSpace(l.Description))
                            throw new BusinessRuleException("pur.line_description", "Describe the service.", "أدخل وصف الخدمة.");
                        break;
                    case InvoiceLineType.LandedCost:
                        if (string.IsNullOrWhiteSpace(l.Description))
                            throw new BusinessRuleException("pur.line_description", "Describe the charge (freight, customs duty…).", "أدخل وصف التكلفة.");
                        itemId = null;
                        poLineId = null;
                        break;
                }

                var category = supplier.SubjectToWht && l.LineType != InvoiceLineType.Goods
                    ? l.WhtCategory ?? (poWht != WhtCategory.None ? poWht : supplier.DefaultWhtCategory)
                    : WhtCategory.None;
                var rate = PurchasingRules.WhtRate(category);
                invoice.Lines.Add(new SupplierInvoiceLine
                {
                    SupplierInvoiceId = invoice.Id, LineNo = ++lineNo, LineType = l.LineType, GoodsReceiptLineId = l.GoodsReceiptLineId,
                    PurchaseOrderLineId = poLineId, ItemId = itemId, Description = SupplierService.Clean(l.Description), Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice, Amount = amount, ReceiptUnitCost = receiptCost, Variance = lineVariance, WhtCategory = category,
                    WhtRate = rate, WhtAmount = Money.Round(amount * rate / 100m)
                });
            }
            invoice.Total = invoice.Lines.Sum(l => l.Amount);
            invoice.TotalBase = invoice.Lines.Sum(l => PurchasingRules.ToBase(l.Amount, fx));
            if (invoice.Total <= 0)
                throw new BusinessRuleException("pur.invoice_total", "The invoice total must be positive.", "يجب أن يكون إجمالي الفاتورة موجباً.");
            invoice.WhtAmount = invoice.Lines.Sum(l => l.WhtAmount);
            invoice.MatchStatus = invoice.Lines.Any(l => l.LineType == InvoiceLineType.Goods)
                ? variance ? MatchStatus.Variance : MatchStatus.Matched
                : MatchStatus.NotApplicable;
            db.Add(invoice);
            await db.SaveChangesAsync(token);
            return invoice;
        }, ct);

    public async Task<SupplierInvoice> ApproveVarianceAsync(Guid id, CancellationToken ct)
    {
        var invoice = await FindAsync(id, ct);
        if (invoice.Status != InvoiceStatus.Draft || invoice.MatchStatus != MatchStatus.Variance)
            throw new ConflictException("pur.no_variance", "This invoice has no price variance waiting for approval.", "لا توجد فروقات أسعار بانتظار الاعتماد.");
        invoice.MatchStatus = MatchStatus.Approved;
        invoice.VarianceApprovedBy = tenant.ActingUserId;
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        var invoice = await FindAsync(id, ct);
        if (invoice.Status != InvoiceStatus.Draft)
            throw new ConflictException("pur.invoice_not_draft", "Only draft invoices can be deleted.", "يمكن حذف الفواتير المسودة فقط.");
        db.RemoveRange(invoice.Lines);
        db.Remove(invoice);
        await db.SaveChangesAsync(ct);
    }

    public Task<SupplierInvoice> PostAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await FindAsync(id, token);
            if (invoice.Status != InvoiceStatus.Draft)
                throw new ConflictException("pur.invoice_not_draft", "This invoice is already posted.", "تم ترحيل هذه الفاتورة مسبقاً.");
            if (invoice.MatchStatus == MatchStatus.Variance)
                throw new BusinessRuleException("pur.variance_hold", "The invoice price exceeds the receipt cost beyond the tolerance and needs approval.",
                    "سعر الفاتورة يتجاوز تكلفة الاستلام بأكثر من النسبة المسموحة ويحتاج إلى اعتماد.");

            var grnLines = await LinesAsync<GoodsReceiptLine>(invoice.Lines.Select(l => l.GoodsReceiptLineId), token);
            var poLines = await LinesAsync<PurchaseOrderLine>(invoice.Lines.Select(l => l.PurchaseOrderLineId), token);
            var items = await inventory.GetItemsAsync(invoice.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), token);
            var builder = new PostingBuilder();
            var revaluations = new List<RevaluationLine>();
            foreach (var l in invoice.Lines)
            {
                switch (l.LineType)
                {
                    case InvoiceLineType.Goods:
                        var grn = grnLines[l.GoodsReceiptLineId!.Value];
                        if (grn.QuantityInvoiced + l.Quantity > grn.Quantity)
                            throw new BusinessRuleException("pur.over_invoice", $"Line {l.LineNo}: the receipt has already been invoiced.",
                                $"السطر {l.LineNo}: تمت فوترة الاستلام مسبقاً.");
                        grn.QuantityInvoiced += l.Quantity;
                        builder.Add(AccountRoles.GoodsReceivedNotInvoiced, null, PurchasingRules.ToBase(l.Amount, invoice.ExchangeRate) - l.Variance,
                            invoice.SupplierId);
                        if (l.Variance != 0) revaluations.Add(new RevaluationLine(l.ItemId!.Value, l.Variance, l.Quantity, l.Id));
                        break;
                    case InvoiceLineType.Service:
                        builder.Add(AccountRoles.PurchaseExpense, l.ItemId is { } item ? items[item].ItemGroupId : null,
                            PurchasingRules.ToBase(l.Amount, invoice.ExchangeRate));
                        break;
                    case InvoiceLineType.LandedCost:
                        builder.Add(AccountRoles.LandedCostClearing, null, PurchasingRules.ToBase(l.Amount, invoice.ExchangeRate));
                        break;
                }
                if (l.PurchaseOrderLineId is { } poLine) poLines[poLine].QuantityInvoiced += l.Quantity;
            }
            foreach (var x in await stock.RevalueAsync(SourceType, invoice.Id, invoice.InvoiceNo, invoice.InvoiceDate, revaluations, token))
            {
                builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.ToInventory);
                builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, x.ToCostOfSales);
            }
            builder.Add(AccountRoles.AccountsPayable, null, -invoice.TotalBase, invoice.SupplierId);

            var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == invoice.SupplierId, token);
            invoice.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = invoice.Id, SourceNo = invoice.InvoiceNo, PostingDate = invoice.InvoiceDate,
                MemoEn = $"Supplier invoice {invoice.SupplierRef} — {supplier.NameEn}{FxMemo(invoice.Currency, invoice.Total, invoice.ExchangeRate)}",
                MemoAr = $"فاتورة مورد {invoice.SupplierRef} — {supplier.NameAr}{FxMemo(invoice.Currency, invoice.Total, invoice.ExchangeRate)}",
                Lines = builder.Lines()
            }, token)).JournalEntryId;
            invoice.Status = InvoiceStatus.Posted;
            await db.SaveChangesAsync(token);
            return invoice;
        }, ct);

    public Task<SupplierInvoice> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await FindAsync(id, token);
            if (invoice.Status is not InvoiceStatus.Posted || invoice.DebitedAmount != 0)
                throw new ConflictException("pur.invoice_not_open", "Only posted invoices without payments or debit notes can be reversed (reverse those first).",
                    "يمكن عكس الفواتير المرحّلة التي ليس عليها دفعات أو إشعارات مدينة فقط (اعكسها أولاً).");
            if (await (from c in db.Set<LandedCostCharge>()
                       join v in db.Set<LandedCostVoucher>() on c.LandedCostVoucherId equals v.Id
                       where c.SupplierInvoiceId == invoice.Id && v.Status == DocumentStatus.Posted
                       select c.Id).AnyAsync(token))
                throw new BusinessRuleException("pur.invoice_landed", "Reverse the landed cost voucher that uses this invoice first.",
                    "قم أولاً بعكس قسيمة تكاليف الاستيراد المرتبطة بهذه الفاتورة.");

            await stock.ReverseAsync(SourceType, invoice.Id, r.Date, token);
            await posting.ReverseAsync(SourceType, invoice.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var grnLines = await LinesAsync<GoodsReceiptLine>(invoice.Lines.Select(l => l.GoodsReceiptLineId), token);
            var poLines = await LinesAsync<PurchaseOrderLine>(invoice.Lines.Select(l => l.PurchaseOrderLineId), token);
            foreach (var l in invoice.Lines)
            {
                if (l.GoodsReceiptLineId is { } g) grnLines[g].QuantityInvoiced -= l.Quantity;
                if (l.PurchaseOrderLineId is { } p) poLines[p].QuantityInvoiced -= l.Quantity;
            }
            invoice.Status = InvoiceStatus.Reversed;
            await db.SaveChangesAsync(token);
            return invoice;
        }, ct);

    /// <summary>Foreign-currency documents show their original amount and rate in the journal memo.</summary>
    internal static string FxMemo(string currency, decimal amount, decimal rate) =>
        currency == Money.BaseCurrency ? "" : $" ({currency} {amount:N2} @ {rate:0.######})";

    /// <summary>Paid once payments and debit notes cover the total; partly paid once anything has been settled.</summary>
    internal static void RefreshPaymentStatus(SupplierInvoice invoice) =>
        invoice.Status = invoice.PaidAmount + invoice.DebitedAmount >= invoice.Total ? InvoiceStatus.Paid
            : invoice.PaidAmount + invoice.DebitedAmount > 0 ? InvoiceStatus.PartiallyPaid
            : InvoiceStatus.Posted;

    private async Task<Dictionary<Guid, T>> LinesAsync<T>(IEnumerable<Guid?> ids, CancellationToken ct) where T : Entity
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? [] : await db.Set<T>().Where(l => list.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
    }

    private async Task<SupplierInvoice> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<SupplierInvoice>().Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id && i.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Supplier invoice", id);
}

/// <summary>
/// Landed cost: freight, clearing, customs duty and insurance (booked to the clearing account by their supplier invoices)
/// are allocated to receipt lines by value or quantity. The share for stock still on hand raises its average cost; the
/// share for quantities already sold goes to cost of sales.
/// </summary>
internal sealed class LandedCostService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IStockService stock, IPostingService posting)
{
    public const string SourceType = "landed_cost";

    public Task<LandedCostVoucher> CreateAsync(LandedCostRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            if (r.Charges is not { Count: > 0 } || r.Charges.Any(c => c.Amount <= 0))
                throw new BusinessRuleException("pur.charges", "Enter the charges with positive amounts.", "أدخل التكاليف بمبالغ موجبة.");
            var receipts = await db.Set<GoodsReceipt>().AsNoTracking().Include(g => g.Lines)
                .Where(g => r.GoodsReceiptIds.Contains(g.Id) && g.CompanyId == companyId).ToListAsync(token);
            if (receipts.Count != r.GoodsReceiptIds.Distinct().Count() || receipts.Any(g => g.Status != DocumentStatus.Posted))
                throw new BusinessRuleException("pur.landed_receipts", "Choose posted goods receipts.", "اختر استلامات بضاعة مرحّلة.");

            foreach (var group in r.Charges.Where(c => c.SupplierInvoiceId is not null).GroupBy(c => c.SupplierInvoiceId!.Value))
            {
                var invoice = await db.Set<SupplierInvoice>().AsNoTracking().Include(i => i.Lines)
                                  .SingleOrDefaultAsync(i => i.Id == group.Key && i.CompanyId == companyId, token)
                              ?? throw new NotFoundException("Supplier invoice", group.Key);
                if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Reversed)
                    throw new BusinessRuleException("pur.charge_invoice", $"Invoice {invoice.InvoiceNo} is not posted.", $"الفاتورة {invoice.InvoiceNo} غير مرحّلة.");
                var available = invoice.Lines.Where(l => l.LineType == InvoiceLineType.LandedCost).Sum(l => PurchasingRules.ToBase(l.Amount, invoice.ExchangeRate));
                var used = await (from c in db.Set<LandedCostCharge>()
                                  join v in db.Set<LandedCostVoucher>() on c.LandedCostVoucherId equals v.Id
                                  where c.SupplierInvoiceId == invoice.Id && v.Status == DocumentStatus.Posted
                                  select c.Amount).SumAsync(token);
                if (group.Sum(c => c.Amount) > available - used)
                    throw new BusinessRuleException("pur.charge_exceeds_invoice",
                        $"Invoice {invoice.InvoiceNo} has only {available - used:N2} of landed cost left to allocate.",
                        $"المتبقي للتوزيع من الفاتورة {invoice.InvoiceNo} هو {available - used:N2} فقط.");
            }

            var voucher = new LandedCostVoucher
            {
                CompanyId = companyId,
                VoucherNo = await numbering.NextAsync(SourceType, "LCV", r.Date, ct: token),
                VoucherDate = r.Date,
                Method = r.Method,
                Notes = r.Notes,
                TotalAmount = r.Charges.Sum(c => Money.Round(c.Amount))
            };
            var lineNo = 0;
            foreach (var c in r.Charges)
                voucher.Charges.Add(new LandedCostCharge
                {
                    LandedCostVoucherId = voucher.Id, LineNo = ++lineNo, ChargeType = c.ChargeType, Description = SupplierService.Clean(c.Description),
                    Amount = Money.Round(c.Amount), SupplierInvoiceId = c.SupplierInvoiceId
                });

            var lines = receipts.SelectMany(g => g.Lines).OrderBy(l => l.GoodsReceiptId).ThenBy(l => l.LineNo).ToList();
            var bases = lines.Select(l => r.Method == AllocationMethod.Value ? l.Value : l.Quantity).ToList();
            var parts = PurchasingRules.Allocate(voucher.TotalAmount, bases);
            for (var i = 0; i < lines.Count; i++)
                voucher.Allocations.Add(new LandedCostAllocation
                {
                    LandedCostVoucherId = voucher.Id, GoodsReceiptLineId = lines[i].Id, ItemId = lines[i].ItemId, Basis = bases[i], Amount = parts[i]
                });

            var results = await stock.RevalueAsync(SourceType, voucher.Id, voucher.VoucherNo, r.Date,
                voucher.Allocations.Where(a => a.Amount != 0)
                    .Select(a => new RevaluationLine(a.ItemId, a.Amount, lines.Single(l => l.Id == a.GoodsReceiptLineId).Quantity, a.Id)).ToList(), token);
            var builder = new PostingBuilder();
            foreach (var x in results)
            {
                var allocation = voucher.Allocations.Single(a => a.Id == x.SourceLineId);
                allocation.ToInventory = x.ToInventory;
                allocation.ToCostOfSales = x.ToCostOfSales;
                builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.ToInventory);
                builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, x.ToCostOfSales);
            }
            builder.Add(AccountRoles.LandedCostClearing, null, -voucher.TotalAmount);
            db.Add(voucher);
            voucher.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = voucher.Id, SourceNo = voucher.VoucherNo, PostingDate = r.Date,
                MemoEn = $"Landed cost {voucher.VoucherNo}", MemoAr = $"تكاليف استيراد {voucher.VoucherNo}", Lines = builder.Lines()
            }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return voucher;
        }, ct);

    public Task<LandedCostVoucher> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var voucher = await db.Set<LandedCostVoucher>().Include(v => v.Charges).Include(v => v.Allocations)
                              .SingleOrDefaultAsync(v => v.Id == id && v.CompanyId == tenant.RequireCompanyId(), token)
                          ?? throw new NotFoundException("Landed cost voucher", id);
            if (voucher.Status != DocumentStatus.Posted)
                throw new ConflictException("pur.not_posted", "This voucher is already reversed.", "تم عكس هذه القسيمة مسبقاً.");
            await stock.ReverseAsync(SourceType, voucher.Id, r.Date, token);
            await posting.ReverseAsync(SourceType, voucher.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            voucher.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return voucher;
        }, ct);
}

/// <summary>
/// Pays one or more open invoices of a supplier: debit Accounts Payable (gross), credit the bank (net) and, for
/// non-residents without a PE, credit Withholding Tax Payable with the tax withheld.
/// </summary>
internal sealed class SupplierPaymentService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting,
    IFinanceQueries finance)
{
    public const string SourceType = "supplier_payment";
    public const string ClearSourceType = "supplier_pdc_clear";

    public Task<SupplierPayment> CreateAsync(PaymentRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var supplier = await db.Set<Supplier>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == r.SupplierId && s.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Supplier", r.SupplierId);
            var account = await finance.FindPostableAccountAsync(r.PaymentAccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("pur.payment_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");
            if (r.Allocations is not { Count: > 0 } || r.Allocations.Any(a => a.Amount <= 0) ||
                r.Allocations.Select(a => a.SupplierInvoiceId).Distinct().Count() != r.Allocations.Count)
                throw new BusinessRuleException("pur.payment_allocations", "Allocate positive amounts to the invoices being paid (each once).",
                    "وزّع مبالغ موجبة على الفواتير المدفوعة (كل فاتورة مرة واحدة).");

            var ids = r.Allocations.Select(a => a.SupplierInvoiceId).ToList();
            var invoices = await db.Set<SupplierInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            var currencies = invoices.Values.Select(i => i.Currency).Distinct().ToList();
            if (currencies.Count > 1)
                throw new BusinessRuleException("pur.payment_currencies", "Pay invoices in different currencies separately.", "سدد الفواتير ذات العملات المختلفة بشكل منفصل.");
            var currency = currencies.SingleOrDefault() ?? Money.BaseCurrency;
            var postDated = r.ChequeDate is { } chequeDate && chequeDate > r.Date;
            if (postDated && account.Subtype != "bank")
                throw new BusinessRuleException("pur.pdc_bank", "A post-dated cheque must be drawn on a bank account.", "يجب أن يكون الشيك الآجل مسحوباً على حساب بنكي.");
            if (postDated && string.IsNullOrWhiteSpace(r.ChequeNo))
                throw new BusinessRuleException("pur.pdc_cheque_no", "Enter the cheque number.", "أدخل رقم الشيك.");
            var payment = new SupplierPayment
            {
                CompanyId = supplier.CompanyId,
                PaymentNo = await numbering.NextAsync(SourceType, "SPAY", r.Date, ct: token),
                SupplierId = supplier.Id,
                PaymentDate = r.Date,
                PaymentAccountId = account.Id,
                Reference = SupplierService.Clean(r.Reference),
                Currency = currency,
                ExchangeRate = PurchasingRules.ExchangeRate(currency, r.ExchangeRate),
                ChequeNo = SupplierService.Clean(r.ChequeNo),
                ChequeDate = r.ChequeDate,
                PdcStatus = postDated ? PdcStatus.Issued : null
            };
            foreach (var a in r.Allocations)
            {
                if (!invoices.TryGetValue(a.SupplierInvoiceId, out var invoice) || invoice.SupplierId != supplier.Id)
                    throw new NotFoundException("Supplier invoice", a.SupplierInvoiceId);
                if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid))
                    throw new BusinessRuleException("pur.invoice_not_open", $"Invoice {invoice.InvoiceNo} is not open for payment.",
                        $"الفاتورة {invoice.InvoiceNo} غير مفتوحة للدفع.");
                var amount = Money.Round(a.Amount);
                if (amount > invoice.OpenAmount)
                    throw new BusinessRuleException("pur.overpayment", $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} open.",
                        $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} فقط.");
                var wht = PurchasingRules.WhtOnPayment(invoice.Total - invoice.DebitedAmount, invoice.WhtAmount, invoice.WhtWithheld, invoice.OpenAmount, amount);
                var cleared = PurchasingRules.BaseCleared(amount, invoice.OpenAmount, invoice.OpenBase, invoice.ExchangeRate);
                invoice.PaidAmount += amount;
                invoice.PaidBase += cleared;
                invoice.WhtWithheld += wht;
                SupplierInvoiceService.RefreshPaymentStatus(invoice);
                payment.Allocations.Add(new SupplierPaymentAllocation
                {
                    SupplierPaymentId = payment.Id, SupplierInvoiceId = invoice.Id, Amount = amount, WhtAmount = wht, BaseCleared = cleared
                });
            }
            payment.GrossAmount = payment.Allocations.Sum(a => a.Amount);
            payment.WhtAmount = payment.Allocations.Sum(a => a.WhtAmount);
            payment.NetAmount = payment.GrossAmount - payment.WhtAmount;
            db.Add(payment);

            // AP is cleared at the invoices' rates, the bank and the tax at today's rate; the difference is an exchange gain or loss.
            var clearedBase = payment.Allocations.Sum(a => a.BaseCleared);
            var netBase = PurchasingRules.ToBase(payment.NetAmount, payment.ExchangeRate);
            var whtBase = PurchasingRules.ToBase(payment.WhtAmount, payment.ExchangeRate);
            payment.FxDifference = clearedBase - netBase - whtBase;
            List<PostingLine> lines =
            [
                new()
                {
                    AccountRole = AccountRoles.AccountsPayable, Side = PostingSide.Debit, Amount = clearedBase, PartyType = PartyType.Supplier,
                    PartyId = supplier.Id
                },
                postDated
                    ? new() { AccountRole = AccountRoles.PdcPayable, Side = PostingSide.Credit, Amount = netBase, Description = $"Cheque {payment.ChequeNo}" }
                    : new() { AccountId = account.Id, Side = PostingSide.Credit, Amount = netBase }
            ];
            if (whtBase > 0)
                lines.Add(new PostingLine
                {
                    AccountRole = AccountRoles.WithholdingTaxPayable, Side = PostingSide.Credit, Amount = whtBase,
                    PartyType = PartyType.Supplier, PartyId = supplier.Id
                });
            if (payment.FxDifference != 0)
                lines.Add(new PostingLine
                {
                    AccountRole = AccountRoles.ExchangeDifference, Side = payment.FxDifference > 0 ? PostingSide.Credit : PostingSide.Debit,
                    Amount = Math.Abs(payment.FxDifference), Description = "Exchange difference"
                });
            payment.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = payment.Id, SourceNo = payment.PaymentNo, PostingDate = r.Date,
                MemoEn = $"Payment to {supplier.NameEn}{SupplierInvoiceService.FxMemo(payment.Currency, payment.GrossAmount, payment.ExchangeRate)}",
                MemoAr = $"دفعة إلى {supplier.NameAr}{SupplierInvoiceService.FxMemo(payment.Currency, payment.GrossAmount, payment.ExchangeRate)}", Lines = lines
            }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return payment;
        }, ct);

    /// <summary>The supplier presented the cheque and the bank paid it: debit PDC payable, credit the bank.</summary>
    public Task<SupplierPayment> ClearAsync(Guid id, PdcClearRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var payment = await FindAsync(id, token);
            if (payment.Status != DocumentStatus.Posted || payment.PdcStatus != PdcStatus.Issued)
                throw new ConflictException("pur.pdc_not_issued", "Only post-dated cheques not yet cleared can be cleared.", "يمكن تسوية الشيكات الآجلة غير المصروفة فقط.");
            if (r.Date < payment.ChequeDate)
                throw new BusinessRuleException("pur.pdc_early", $"The cheque is dated {payment.ChequeDate:dd/MM/yyyy}.", $"تاريخ الشيك {payment.ChequeDate:dd/MM/yyyy}.");
            var amount = PurchasingRules.ToBase(payment.NetAmount, payment.ExchangeRate);
            await posting.PostAsync(new PostingRequest
            {
                SourceType = ClearSourceType, SourceId = payment.Id, SourceNo = payment.PaymentNo, PostingDate = r.Date,
                MemoEn = $"Cheque {payment.ChequeNo} cleared", MemoAr = $"صرف الشيك {payment.ChequeNo}",
                Lines =
                [
                    new PostingLine { AccountRole = AccountRoles.PdcPayable, Side = PostingSide.Debit, Amount = amount },
                    new PostingLine { AccountId = payment.PaymentAccountId, Side = PostingSide.Credit, Amount = amount }
                ]
            }, token);
            payment.PdcStatus = PdcStatus.Cleared;
            payment.PdcClearedDate = r.Date;
            await db.SaveChangesAsync(token);
            return payment;
        }, ct);

    /// <summary>Cancels a payment entered by mistake, or a cheque stopped before (or returned after) clearing; the invoices reopen.</summary>
    public Task<SupplierPayment> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var payment = await FindAsync(id, token);
            if (payment.Status != DocumentStatus.Posted)
                throw new ConflictException("pur.not_posted", "This payment is already reversed.", "تم عكس هذه الدفعة مسبقاً.");
            var reason = Guard.Required(r.Reason, "reason");
            if (payment.PdcStatus == PdcStatus.Cleared) await posting.ReverseAsync(ClearSourceType, payment.Id, r.Date, reason, token);
            await posting.ReverseAsync(SourceType, payment.Id, r.Date, reason, token);
            var ids = payment.Allocations.Select(a => a.SupplierInvoiceId).ToList();
            var invoices = await db.Set<SupplierInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            foreach (var a in payment.Allocations)
            {
                var invoice = invoices[a.SupplierInvoiceId];
                invoice.PaidAmount -= a.Amount;
                invoice.PaidBase -= a.BaseCleared;
                invoice.WhtWithheld -= a.WhtAmount;
                SupplierInvoiceService.RefreshPaymentStatus(invoice);
            }
            payment.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return payment;
        }, ct);

    private async Task<SupplierPayment> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<SupplierPayment>().Include(p => p.Allocations).SingleOrDefaultAsync(p => p.Id == id && p.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Supplier payment", id);
}

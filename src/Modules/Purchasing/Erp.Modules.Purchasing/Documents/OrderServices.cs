using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Documents;

public sealed record SupplierRequest(string Code, string? NameEn, string? NameAr, string? Country, bool IsResident, bool HasPermanentEstablishment,
    string? TaxCardNo, string? CrNo, string? Email, string? Phone, int PaymentTermsDays = 30, WhtCategory DefaultWhtCategory = WhtCategory.None,
    bool IsActive = true, string? Currency = null);

public sealed record PurchaseSettingsRequest(decimal PriceTolerancePercent);

/// <summary>Quantity and price are in <see cref="UomId"/> when given (an alternative unit of the item), otherwise in its base unit.</summary>
public sealed record OrderLineRequest(Guid ItemId, string? Description, decimal Quantity, decimal UnitPrice, WhtCategory? WhtCategory = null, Guid? UomId = null);

public sealed record OrderRequest(Guid SupplierId, DateOnly OrderDate, DateOnly? ExpectedDate, Guid WarehouseId, string? Notes,
    IReadOnlyList<OrderLineRequest> Lines, string? Currency = null, decimal? ExchangeRate = null);

/// <summary>Batch-tracked items need the supplier's batch (lot) number and, where printed, the expiry date.</summary>
public sealed record ReceiptLineRequest(Guid PurchaseOrderLineId, decimal Quantity, string? BatchNo = null, DateOnly? ExpiryDate = null);

/// <summary>Goods are costed at the order's exchange rate unless a rate for the receipt date is given.</summary>
public sealed record ReceiptRequest(Guid PurchaseOrderId, DateOnly ReceiptDate, Guid? WarehouseId, string? SupplierDeliveryNote,
    IReadOnlyList<ReceiptLineRequest> Lines, decimal? ExchangeRate = null);

public sealed record ReverseRequest(DateOnly Date, string Reason);

public sealed record GtaNotificationRequest(string Reference);

internal sealed class SupplierService(ErpDbContext db, ITenantContext tenant)
{
    public async Task<Supplier> SaveAsync(Guid? id, SupplierRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        var country = string.IsNullOrWhiteSpace(r.Country) ? "QA" : r.Country.Trim().ToUpperInvariant();
        if (country.Length != 2)
            throw new BusinessRuleException("pur.country", "Use the two-letter country code (QA, AE, IN…).", "استخدم رمز الدولة المكون من حرفين.");
        if (r.PaymentTermsDays is < 0 or > 365)
            throw new BusinessRuleException("pur.terms", "Payment terms must be between 0 and 365 days.", "يجب أن تكون مدة السداد بين 0 و365 يوماً.");
        var supplier = id is null
            ? db.Add(new Supplier { CompanyId = tenant.RequireCompanyId(), Code = "", NameEn = en, NameAr = ar }).Entity
            : await db.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct)
              ?? throw new NotFoundException("Supplier", id);
        supplier.Code = Guard.Required(r.Code, "code").ToUpperInvariant();
        supplier.NameEn = en;
        supplier.NameAr = ar;
        supplier.Country = country;
        supplier.IsResident = r.IsResident;
        supplier.HasPermanentEstablishment = !r.IsResident && r.HasPermanentEstablishment;
        supplier.TaxCardNo = Clean(r.TaxCardNo);
        supplier.CrNo = Clean(r.CrNo);
        supplier.Email = Clean(r.Email)?.ToLowerInvariant();
        supplier.Phone = Clean(r.Phone);
        supplier.PaymentTermsDays = r.PaymentTermsDays;
        supplier.Currency = PurchasingRules.CurrencyCode(r.Currency);
        supplier.DefaultWhtCategory = r.DefaultWhtCategory;
        supplier.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return supplier;
    }

    public async Task<PurchaseSettings> SaveSettingsAsync(PurchaseSettingsRequest r, CancellationToken ct)
    {
        var settings = await db.Set<PurchaseSettings>().SingleAsync(s => s.CompanyId == tenant.RequireCompanyId(), ct);
        settings.PriceTolerancePercent = Guard.NonNegative(r.PriceTolerancePercent, "priceTolerancePercent");
        await db.SaveChangesAsync(ct);
        return settings;
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Purchase orders: draft → submitted (approved straight away unless an approval rule applies to the amount) → received → closed.</summary>
internal sealed class PurchaseOrderService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory, IClock clock,
    IApprovalService approvals)
{
    public const string SourceType = "purchase_order";

    public Task<PurchaseOrder> CreateAsync(OrderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = new PurchaseOrder
            {
                CompanyId = tenant.RequireCompanyId(),
                PoNo = await numbering.NextAsync(SourceType, "PO", r.OrderDate, ct: token)
            };
            db.Add(order);
            await ApplyAsync(order, r, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public Task<PurchaseOrder> UpdateAsync(Guid id, OrderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status != PurchaseOrderStatus.Draft)
                throw new ConflictException("pur.po_not_draft", "Only draft orders can be edited.", "يمكن تعديل أوامر الشراء المسودة فقط.");
            db.RemoveRange(order.Lines);
            order.Lines.Clear();
            await db.SaveChangesAsync(token);
            await ApplyAsync(order, r, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    private async Task ApplyAsync(PurchaseOrder order, OrderRequest r, CancellationToken ct)
    {
        var supplier = await ActiveSupplierAsync(db, tenant, r.SupplierId, ct);
        var warehouse = await inventory.GetWarehouseAsync(r.WarehouseId, ct);
        if (warehouse is not { IsActive: true }) throw new NotFoundException("Warehouse", r.WarehouseId);
        if (r.Lines is not { Count: > 0 })
            throw new BusinessRuleException("pur.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");
        var items = await inventory.GetItemsAsync(r.Lines.Select(l => l.ItemId), ct);
        var units = await inventory.GetUnitsAsync(r.Lines.Select(l => l.ItemId), ct);

        order.SupplierId = supplier.Id;
        order.Currency = PurchasingRules.CurrencyCode(r.Currency ?? supplier.Currency);
        order.ExchangeRate = PurchasingRules.ExchangeRate(order.Currency, r.ExchangeRate);
        order.OrderDate = r.OrderDate;
        order.ExpectedDate = r.ExpectedDate;
        order.WarehouseId = r.WarehouseId;
        order.Notes = r.Notes;
        var lineNo = 0;
        foreach (var l in r.Lines)
        {
            if (!items.TryGetValue(l.ItemId, out var item) || !item.IsActive) throw new NotFoundException("Item", l.ItemId);
            if (l.Quantity <= 0 || l.UnitPrice < 0)
                throw new BusinessRuleException("pur.line_values", "Quantities must be positive and prices not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");
            var (uomId, factor) = ItemUnits.Resolve(units, item, l.UomId, "pur");
            var line = new PurchaseOrderLine
            {
                PurchaseOrderId = order.Id,
                LineNo = ++lineNo,
                ItemId = item.Id,
                Description = SupplierService.Clean(l.Description),
                Quantity = l.Quantity * factor,
                UnitPrice = Math.Round(l.UnitPrice / factor, 6, MidpointRounding.AwayFromZero),
                UomId = uomId,
                UomFactor = factor,
                UomQuantity = l.Quantity,
                UomPrice = l.UnitPrice,
                Amount = Money.Round(l.Quantity * l.UnitPrice),
                WhtCategory = l.WhtCategory ?? (item.Type == ItemType.Service ? supplier.DefaultWhtCategory : WhtCategory.None)
            };
            order.Lines.Add(line);
            db.Add(line);
        }
        order.Total = order.Lines.Sum(l => l.Amount);
    }

    public Task<PurchaseOrder> SubmitAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status != PurchaseOrderStatus.Draft)
                throw new ConflictException("pur.po_not_draft", "Only draft orders can be submitted.", "يمكن إرسال أوامر الشراء المسودة فقط.");
            order.SubmittedAt = clock.UtcNow;
            var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == order.SupplierId, token);
            var outcome = await approvals.SubmitAsync(new ApprovalSubmission(SourceType, order.Id, order.PoNo, Money.Round(order.Total * order.ExchangeRate),
                $"{supplier.NameEn} · {order.Currency} {order.Total:N2}", $"{supplier.NameAr} · {order.Currency} {order.Total:N2}"), token);
            order.RequiresApproval = outcome == ApprovalOutcome.Pending;
            if (order.RequiresApproval) order.Status = PurchaseOrderStatus.PendingApproval;
            else await ApproveCoreAsync(order, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    /// <summary>Decides the current approval step from the order screen (same checks as the approvals inbox).</summary>
    public Task<PurchaseOrder> DecideAsync(Guid id, bool approve, string? comment, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status != PurchaseOrderStatus.PendingApproval)
                throw new ConflictException("pur.po_not_pending", "This order is not waiting for approval.", "أمر الشراء ليس بانتظار الاعتماد.");
            await approvals.DecideAsync(SourceType, id, approve, comment, token);
            return await FindAsync(id, token);
        }, ct);

    /// <summary>Called by the approval workflow when the last step is approved.</summary>
    internal async Task OnApprovedAsync(Guid id, CancellationToken ct)
    {
        var order = await FindAsync(id, ct);
        if (order.Status != PurchaseOrderStatus.PendingApproval) return;
        await ApproveCoreAsync(order, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A rejected order goes back to draft for the buyer to change and resubmit.</summary>
    internal async Task OnRejectedAsync(Guid id, CancellationToken ct)
    {
        var order = await FindAsync(id, ct);
        if (order.Status != PurchaseOrderStatus.PendingApproval) return;
        order.Status = PurchaseOrderStatus.Draft;
        await db.SaveChangesAsync(ct);
    }

    public Task<PurchaseOrder> CancelAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Lines.Any(l => l.QuantityReceived > 0 || l.QuantityInvoiced > 0))
                throw new ConflictException("pur.po_has_activity", "Orders with receipts or invoices cannot be cancelled; close them instead.",
                    "لا يمكن إلغاء أمر شراء عليه استلامات أو فواتير؛ قم بإغلاقه بدلاً من ذلك.");
            await approvals.WithdrawAsync(SourceType, id, token);
            return await TransitionAsync(id, PurchaseOrderStatus.Cancelled, token,
                PurchaseOrderStatus.Draft, PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Approved);
        }, ct);

    public Task<PurchaseOrder> CloseAsync(Guid id, CancellationToken ct) => TransitionAsync(id, PurchaseOrderStatus.Closed, ct,
        PurchaseOrderStatus.Approved, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received);

    public async Task<PurchaseOrder> SetGtaNotificationAsync(Guid id, GtaNotificationRequest r, CancellationToken ct)
    {
        var order = await FindAsync(id, ct);
        order.GtaNotificationRef = Guard.Required(r.Reference, "reference");
        await db.SaveChangesAsync(ct);
        return order;
    }

    private async Task ApproveCoreAsync(PurchaseOrder order, CancellationToken ct)
    {
        order.Status = PurchaseOrderStatus.Approved;
        order.ApprovedBy = tenant.ActingUserId;
        order.ApprovedAt = clock.UtcNow;
        var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == order.SupplierId, ct);
        if (!supplier.IsResident) order.GtaNotificationDue = clock.Today.AddDays(30);
    }

    private async Task<PurchaseOrder> TransitionAsync(Guid id, PurchaseOrderStatus to, CancellationToken ct, params PurchaseOrderStatus[] from)
    {
        var order = await FindAsync(id, ct);
        if (!from.Contains(order.Status))
            throw new ConflictException("pur.po_status", $"A {order.Status} order cannot become {to}.", "لا يمكن تغيير حالة أمر الشراء بهذا الشكل.");
        order.Status = to;
        await db.SaveChangesAsync(ct);
        return order;
    }

    private async Task<PurchaseOrder> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id && o.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Purchase order", id);

    internal static async Task<Supplier> ActiveSupplierAsync(ErpDbContext db, ITenantContext tenant, Guid id, CancellationToken ct)
    {
        var supplier = await db.Set<Supplier>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct)
                       ?? throw new NotFoundException("Supplier", id);
        if (!supplier.IsActive)
            throw new BusinessRuleException("pur.supplier_inactive", $"Supplier {supplier.Code} is inactive.", $"المورد {supplier.Code} غير نشط.");
        return supplier;
    }

    /// <summary>Received when every stock line is fully received; partially received once anything has arrived.</summary>
    internal static void RefreshReceiptStatus(PurchaseOrder order, IReadOnlyDictionary<Guid, ItemInfo> items)
    {
        if (order.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received)) return;
        var stockLines = order.Lines.Where(l => items.TryGetValue(l.ItemId, out var i) && i.Type == ItemType.Stock).ToList();
        order.Status = stockLines.Count > 0 && stockLines.All(l => l.QuantityReceived >= l.Quantity) ? PurchaseOrderStatus.Received
            : order.Lines.Any(l => l.QuantityReceived > 0) ? PurchaseOrderStatus.PartiallyReceived
            : PurchaseOrderStatus.Approved;
    }
}

/// <summary>Goods receipts put stock in at the order price: debit Inventory, credit Goods Received Not Invoiced.</summary>
internal sealed class GoodsReceiptService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IStockService stock, IPostingService posting)
{
    public const string SourceType = "goods_receipt";

    public Task<GoodsReceipt> CreateAsync(ReceiptRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await db.Set<PurchaseOrder>().Include(o => o.Lines)
                            .SingleOrDefaultAsync(o => o.Id == r.PurchaseOrderId && o.CompanyId == tenant.RequireCompanyId(), token)
                        ?? throw new NotFoundException("Purchase order", r.PurchaseOrderId);
            if (order.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))
                throw new BusinessRuleException("pur.po_not_receivable", "Goods can only be received against an approved, open order.",
                    "يمكن استلام البضاعة فقط على أمر شراء معتمد ومفتوح.");
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0))
                throw new BusinessRuleException("pur.receipt_lines", "Enter the quantities received.", "أدخل الكميات المستلمة.");
            var items = await inventory.GetItemsAsync(order.Lines.Select(l => l.ItemId), token);
            var warehouseId = r.WarehouseId ?? order.WarehouseId;
            var rate = r.ExchangeRate is null ? order.ExchangeRate : PurchasingRules.ExchangeRate(order.Currency, r.ExchangeRate);

            var receipt = new GoodsReceipt
            {
                CompanyId = order.CompanyId,
                GrnNo = await numbering.NextAsync(SourceType, "GRN", r.ReceiptDate, ct: token),
                PurchaseOrderId = order.Id,
                SupplierId = order.SupplierId,
                WarehouseId = warehouseId,
                ReceiptDate = r.ReceiptDate,
                ExchangeRate = rate,
                SupplierDeliveryNote = SupplierService.Clean(r.SupplierDeliveryNote)
            };
            var lineNo = 0;
            foreach (var l in r.Lines)
            {
                var poLine = order.Lines.SingleOrDefault(x => x.Id == l.PurchaseOrderLineId) ?? throw new NotFoundException("Order line", l.PurchaseOrderLineId);
                if (items[poLine.ItemId].Type != ItemType.Stock)
                    throw new BusinessRuleException("pur.receipt_service", "Service lines are invoiced directly, not received.",
                        "تُفوتر بنود الخدمات مباشرة ولا تُستلم.");
                if (poLine.QuantityReceived + l.Quantity > poLine.Quantity)
                    throw new BusinessRuleException("pur.over_receipt",
                        $"Line {poLine.LineNo}: only {poLine.Quantity - poLine.QuantityReceived:0.##} left to receive.",
                        $"السطر {poLine.LineNo}: المتبقي للاستلام {poLine.Quantity - poLine.QuantityReceived:0.##} فقط.");
                if (items[poLine.ItemId].TrackBatches && string.IsNullOrWhiteSpace(l.BatchNo))
                    throw new BusinessRuleException("pur.batch_required", $"Line {poLine.LineNo}: enter the batch number of {items[poLine.ItemId].Sku}.",
                        $"السطر {poLine.LineNo}: أدخل رقم الدفعة للصنف {items[poLine.ItemId].Sku}.");
                poLine.QuantityReceived += l.Quantity;
                receipt.Lines.Add(new GoodsReceiptLine
                {
                    GoodsReceiptId = receipt.Id, LineNo = ++lineNo, PurchaseOrderLineId = poLine.Id, ItemId = poLine.ItemId, Quantity = l.Quantity,
                    UnitCost = Math.Round(poLine.UnitPrice * rate, 6, MidpointRounding.AwayFromZero)
                });
            }
            db.Add(receipt);

            var requested = r.Lines.ToList();
            var results = await stock.PostAsync(new StockPosting(SourceType, receipt.Id, receipt.GrnNo, r.ReceiptDate,
                receipt.Lines.OrderBy(l => l.LineNo).Select((l, i) => new StockLine(l.ItemId, warehouseId, l.Quantity, l.UnitCost, l.Id,
                    Batches: string.IsNullOrWhiteSpace(requested[i].BatchNo) ? null : [new BatchQuantity(requested[i].BatchNo!, l.Quantity, requested[i].ExpiryDate)]))
                    .ToList()), token);
            foreach (var x in results) receipt.Lines.Single(l => l.Id == x.SourceLineId).Value = x.Value;
            receipt.TotalValue = results.Sum(x => x.Value);

            var lines = new List<PostingLine>();
            foreach (var g in results.GroupBy(x => x.ItemGroupId).Where(g => g.Sum(x => x.Value) != 0))
                lines.Add(new PostingLine { AccountRole = AccountRoles.Inventory, RoleKey = g.Key, Side = PostingSide.Debit, Amount = g.Sum(x => x.Value) });
            if (lines.Count > 0)
            {
                lines.Add(new PostingLine
                {
                    AccountRole = AccountRoles.GoodsReceivedNotInvoiced, Side = PostingSide.Credit, Amount = receipt.TotalValue,
                    PartyType = PartyType.Supplier, PartyId = order.SupplierId
                });
                receipt.JournalEntryId = (await posting.PostAsync(new PostingRequest
                {
                    SourceType = SourceType, SourceId = receipt.Id, SourceNo = receipt.GrnNo, PostingDate = r.ReceiptDate,
                    MemoEn = $"Goods received {receipt.GrnNo} against {order.PoNo}", MemoAr = $"استلام بضاعة {receipt.GrnNo} لأمر الشراء {order.PoNo}",
                    Lines = lines
                }, token)).JournalEntryId;
            }
            PurchaseOrderService.RefreshReceiptStatus(order, items);
            await db.SaveChangesAsync(token);
            return receipt;
        }, ct);

    public Task<GoodsReceipt> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var receipt = await db.Set<GoodsReceipt>().Include(g => g.Lines)
                              .SingleOrDefaultAsync(g => g.Id == id && g.CompanyId == tenant.RequireCompanyId(), token)
                          ?? throw new NotFoundException("Goods receipt", id);
            if (receipt.Status != DocumentStatus.Posted)
                throw new ConflictException("pur.not_posted", "This receipt is already reversed.", "تم عكس هذا الاستلام مسبقاً.");
            if (receipt.Lines.Any(l => l.QuantityInvoiced > 0))
                throw new BusinessRuleException("pur.receipt_invoiced", "Reverse the supplier invoice first.", "قم بعكس فاتورة المورد أولاً.");
            var lineIds = receipt.Lines.Select(l => l.Id).ToList();
            if (await (from a in db.Set<LandedCostAllocation>()
                       join v in db.Set<LandedCostVoucher>() on a.LandedCostVoucherId equals v.Id
                       where lineIds.Contains(a.GoodsReceiptLineId) && v.Status == DocumentStatus.Posted
                       select a.Id).AnyAsync(token))
                throw new BusinessRuleException("pur.receipt_landed", "Reverse the landed cost voucher first.", "قم بعكس قسيمة تكاليف الاستيراد أولاً.");

            await stock.ReverseAsync(SourceType, receipt.Id, r.Date, token);
            if (receipt.JournalEntryId is not null) await posting.ReverseAsync(SourceType, receipt.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var order = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == receipt.PurchaseOrderId, token);
            foreach (var l in receipt.Lines) order.Lines.Single(x => x.Id == l.PurchaseOrderLineId).QuantityReceived -= l.Quantity;
            PurchaseOrderService.RefreshReceiptStatus(order, await inventory.GetItemsAsync(order.Lines.Select(l => l.ItemId), token));
            receipt.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return receipt;
        }, ct);
}

/// <summary>Purchase orders in the shared approval workflow.</summary>
internal sealed class PurchaseOrderApproval(PurchaseOrderService orders) : IApprovalDocument
{
    public string DocType => PurchaseOrderService.SourceType;
    public string NameEn => "Purchase order";
    public string NameAr => "أمر شراء";
    public IReadOnlyList<ApprovalStepRule> DefaultSteps => [new(10_000m, DefaultRoles.GeneralManager)];
    public Task OnApprovedAsync(Guid docId, CancellationToken ct) => orders.OnApprovedAsync(docId, ct);
    public Task OnRejectedAsync(Guid docId, string? comment, CancellationToken ct) => orders.OnRejectedAsync(docId, ct);
}

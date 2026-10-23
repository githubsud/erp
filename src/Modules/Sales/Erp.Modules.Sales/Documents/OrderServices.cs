using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

/// <summary>
/// A sales line, in <see cref="UomId"/> when given (an alternative unit of the item) or its base unit. Without a unit price the
/// customer's price (price list, then item sales price) is used, scaled to the unit.
/// </summary>
public sealed record SalesLineRequest(Guid ItemId, string? Description, decimal Quantity, decimal? UnitPrice = null, decimal DiscountPercent = 0,
    Guid? UomId = null);

/// <summary>Currency defaults to the customer's; a foreign currency needs the exchange rate to QAR.</summary>
public sealed record QuotationRequest(Guid CustomerId, DateOnly QuoteDate, DateOnly? ValidUntil, string? Notes, IReadOnlyList<SalesLineRequest> Lines,
    string? Currency = null, decimal? ExchangeRate = null);

public sealed record ConvertQuotationRequest(Guid WarehouseId, DateOnly? OrderDate, DateOnly? ExpectedDate, string? CustomerRef);

public sealed record SalesOrderRequest(Guid CustomerId, DateOnly OrderDate, DateOnly? ExpectedDate, Guid WarehouseId, string? CustomerRef, string? Notes,
    IReadOnlyList<SalesLineRequest> Lines, string? Currency = null, decimal? ExchangeRate = null);

/// <summary><see cref="Serials"/>: the units handed over, for serial-numbered items (otherwise taken in serial order).</summary>
public sealed record DeliveryLineRequest(Guid SalesOrderLineId, decimal Quantity, IReadOnlyList<string>? Serials = null);

public sealed record DeliveryRequest(Guid SalesOrderId, DateOnly DeliveryDate, Guid? WarehouseId, string? DeliveryAddress, string? VehicleNo, string? Notes,
    IReadOnlyList<DeliveryLineRequest> Lines);

/// <summary>Quantity and unit price in the base unit; the entered unit, factor, quantity and price alongside.</summary>
internal sealed record PricedLine(Guid ItemId, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal Amount,
    Guid? UomId, decimal UomFactor, decimal UomQuantity, decimal UomPrice);

internal static class SalesLines
{
    /// <summary>Validates items (active) and prices each line; missing prices come from the customer's price list or the item.</summary>
    public static async Task<List<PricedLine>> PriceAsync(CustomerService customers, IInventoryQueries inventory, Guid customerId,
        IReadOnlyList<SalesLineRequest>? lines, CancellationToken ct, decimal exchangeRate = 1m)
    {
        if (lines is not { Count: > 0 })
            throw new BusinessRuleException("sal.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");
        var ids = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await inventory.GetItemsAsync(ids, ct);
        var units = await inventory.GetUnitsAsync(ids, ct);
        var keys = lines.Where(l => l.UnitPrice is null && items.ContainsKey(l.ItemId))
            .Select(l => (l.ItemId, ItemUnits.Resolve(units, items[l.ItemId], l.UomId, "sal").UomId)).ToList();
        IReadOnlyDictionary<(Guid, Guid?), decimal?> prices = keys.Count > 0 ? await customers.UnitPricesAsync(customerId, keys, ct, exchangeRate) : new Dictionary<(Guid, Guid?), decimal?>();
        return lines.Select(l =>
        {
            if (!items.TryGetValue(l.ItemId, out var item) || !item.IsActive) throw new NotFoundException("Item", l.ItemId);
            var (uomId, factor) = ItemUnits.Resolve(units, item, l.UomId, "sal");
            var price = l.UnitPrice ?? prices.GetValueOrDefault((l.ItemId, uomId))
                        ?? throw new BusinessRuleException("sal.price_missing", $"Enter a price for {item.Sku}: it has no sales price.",
                            $"أدخل سعراً للصنف {item.Sku}: لا يوجد له سعر بيع.");
            return new PricedLine(item.Id, CustomerService.Clean(l.Description), l.Quantity * factor, Math.Round(price / factor, 6, MidpointRounding.AwayFromZero),
                l.DiscountPercent, SalesRules.LineAmount(l.Quantity, price, l.DiscountPercent), uomId, factor, l.Quantity, price);
        }).ToList();
    }
}

/// <summary>Quotations: draft → sent → accepted (converted to a sales order) or rejected. Editable until accepted.</summary>
internal sealed class QuotationService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    CustomerService customers, SalesOrderService orders, IClock clock)
{
    public const string SourceType = "quotation";

    public Task<Quotation> CreateAsync(QuotationRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var quote = new Quotation { CompanyId = tenant.RequireCompanyId(), QuoteNo = await numbering.NextAsync(SourceType, "QT", r.QuoteDate, ct: token) };
            db.Add(quote);
            await ApplyAsync(quote, r, token);
            await db.SaveChangesAsync(token);
            return quote;
        }, ct);

    public Task<Quotation> UpdateAsync(Guid id, QuotationRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var quote = await FindAsync(id, token);
            if (quote.Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
                throw new ConflictException("sal.quote_closed", "Accepted or rejected quotations cannot be edited.", "لا يمكن تعديل عرض سعر مقبول أو مرفوض.");
            db.RemoveRange(quote.Lines);
            quote.Lines.Clear();
            await db.SaveChangesAsync(token);
            await ApplyAsync(quote, r, token);
            await db.SaveChangesAsync(token);
            return quote;
        }, ct);

    private async Task ApplyAsync(Quotation quote, QuotationRequest r, CancellationToken ct)
    {
        var customer = await CustomerService.ActiveCustomerAsync(db, tenant, r.CustomerId, ct);
        var settings = await db.Set<SalesSettings>().AsNoTracking().SingleAsync(s => s.CompanyId == customer.CompanyId, ct);
        quote.Currency = SalesRules.CurrencyCode(r.Currency ?? customer.Currency);
        quote.ExchangeRate = SalesRules.ExchangeRate(quote.Currency, r.ExchangeRate);
        var lines = await SalesLines.PriceAsync(customers, inventory, customer.Id, r.Lines, ct, quote.ExchangeRate);
        quote.CustomerId = customer.Id;
        quote.QuoteDate = r.QuoteDate;
        quote.ValidUntil = r.ValidUntil ?? r.QuoteDate.AddDays(settings.QuotationValidityDays);
        if (quote.ValidUntil < quote.QuoteDate)
            throw new BusinessRuleException("sal.quote_validity", "The validity date cannot be before the quotation date.", "لا يمكن أن يسبق تاريخ الصلاحية تاريخ العرض.");
        quote.Notes = CustomerService.Clean(r.Notes);
        var lineNo = 0;
        foreach (var l in lines)
        {
            var line = new QuotationLine
            {
                QuotationId = quote.Id, LineNo = ++lineNo, ItemId = l.ItemId, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent, Amount = l.Amount, UomId = l.UomId, UomFactor = l.UomFactor, UomQuantity = l.UomQuantity, UomPrice = l.UomPrice
            };
            quote.Lines.Add(line);
            db.Add(line);
        }
        quote.Total = quote.Lines.Sum(l => l.Amount);
    }

    public Task<Quotation> MarkSentAsync(Guid id, CancellationToken ct) => TransitionAsync(id, QuotationStatus.Sent, ct, QuotationStatus.Draft);

    public Task<Quotation> RejectAsync(Guid id, CancellationToken ct) =>
        TransitionAsync(id, QuotationStatus.Rejected, ct, QuotationStatus.Draft, QuotationStatus.Sent);

    /// <summary>Creates a draft sales order with the quotation's lines and prices and marks the quotation accepted.</summary>
    public Task<SalesOrder> ConvertAsync(Guid id, ConvertQuotationRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var quote = await FindAsync(id, token);
            if (quote.Status is not (QuotationStatus.Draft or QuotationStatus.Sent))
                throw new ConflictException("sal.quote_closed", "This quotation is already accepted or rejected.", "عرض السعر مقبول أو مرفوض مسبقاً.");
            var orderDate = r.OrderDate ?? clock.Today;
            if (orderDate > quote.ValidUntil)
                throw new BusinessRuleException("sal.quote_expired", $"The quotation expired on {quote.ValidUntil:dd/MM/yyyy}; extend its validity first.",
                    $"انتهت صلاحية عرض السعر في {quote.ValidUntil:dd/MM/yyyy}؛ مدد صلاحيته أولاً.");
            var order = await orders.CreateAsync(new SalesOrderRequest(quote.CustomerId, orderDate, r.ExpectedDate, r.WarehouseId, r.CustomerRef, quote.Notes,
                quote.Lines.OrderBy(l => l.LineNo)
                    .Select(l => new SalesLineRequest(l.ItemId, l.Description, l.UomQuantity, l.UomPrice, l.DiscountPercent, l.UomId)).ToList(),
                quote.Currency, quote.ExchangeRate),
                token);
            order.QuotationId = quote.Id;
            quote.Status = QuotationStatus.Accepted;
            quote.SalesOrderId = order.Id;
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        var quote = await FindAsync(id, ct);
        if (quote.Status != QuotationStatus.Draft)
            throw new ConflictException("sal.quote_not_draft", "Only draft quotations can be deleted.", "يمكن حذف عروض الأسعار المسودة فقط.");
        db.RemoveRange(quote.Lines);
        db.Remove(quote);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Quotation> TransitionAsync(Guid id, QuotationStatus to, CancellationToken ct, params QuotationStatus[] from)
    {
        var quote = await FindAsync(id, ct);
        if (!from.Contains(quote.Status))
            throw new ConflictException("sal.quote_status", $"A {quote.Status} quotation cannot become {to}.", "لا يمكن تغيير حالة عرض السعر بهذا الشكل.");
        quote.Status = to;
        await db.SaveChangesAsync(ct);
        return quote;
    }

    private async Task<Quotation> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<Quotation>().Include(q => q.Lines).SingleOrDefaultAsync(q => q.Id == id && q.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Quotation", id);
}

/// <summary>
/// Sales orders: draft → confirmed (credit checked: over the limit it is held for a manager under the "block" policy, or confirmed
/// with a warning under "warn") → delivered → closed.
/// </summary>
internal sealed class SalesOrderService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    CustomerService customers, CreditControlService credit, IClock clock)
{
    public const string SourceType = "sales_order";

    public Task<SalesOrder> CreateAsync(SalesOrderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = new SalesOrder { CompanyId = tenant.RequireCompanyId(), SoNo = await numbering.NextAsync(SourceType, "SO", r.OrderDate, ct: token) };
            db.Add(order);
            await ApplyAsync(order, r, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public Task<SalesOrder> UpdateAsync(Guid id, SalesOrderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status != SalesOrderStatus.Draft)
                throw new ConflictException("sal.so_not_draft", "Only draft orders can be edited.", "يمكن تعديل أوامر البيع المسودة فقط.");
            db.RemoveRange(order.Lines);
            order.Lines.Clear();
            await db.SaveChangesAsync(token);
            await ApplyAsync(order, r, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    private async Task ApplyAsync(SalesOrder order, SalesOrderRequest r, CancellationToken ct)
    {
        var customer = await CustomerService.ActiveCustomerAsync(db, tenant, r.CustomerId, ct);
        var warehouse = await inventory.GetWarehouseAsync(r.WarehouseId, ct);
        if (warehouse is not { IsActive: true }) throw new NotFoundException("Warehouse", r.WarehouseId);
        order.Currency = SalesRules.CurrencyCode(r.Currency ?? customer.Currency);
        order.ExchangeRate = SalesRules.ExchangeRate(order.Currency, r.ExchangeRate);
        var lines = await SalesLines.PriceAsync(customers, inventory, customer.Id, r.Lines, ct, order.ExchangeRate);
        order.CustomerId = customer.Id;
        order.OrderDate = r.OrderDate;
        order.ExpectedDate = r.ExpectedDate;
        order.WarehouseId = r.WarehouseId;
        order.CustomerRef = CustomerService.Clean(r.CustomerRef);
        order.Notes = CustomerService.Clean(r.Notes);
        var lineNo = 0;
        foreach (var l in lines)
        {
            var line = new SalesOrderLine
            {
                SalesOrderId = order.Id, LineNo = ++lineNo, ItemId = l.ItemId, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent, Amount = l.Amount, UomId = l.UomId, UomFactor = l.UomFactor, UomQuantity = l.UomQuantity, UomPrice = l.UomPrice
            };
            order.Lines.Add(line);
            db.Add(line);
        }
        order.Total = order.Lines.Sum(l => l.Amount);
    }

    public Task<SalesOrder> ConfirmAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Status != SalesOrderStatus.Draft)
                throw new ConflictException("sal.so_not_draft", "Only draft orders can be confirmed.", "يمكن تأكيد أوامر البيع المسودة فقط.");
            var customer = await CustomerService.ActiveCustomerAsync(db, tenant, order.CustomerId, token);
            var decision = await credit.CheckAsync(customer, SalesRules.ToBase(order.Total, order.ExchangeRate), order.Id, token);
            order.ConfirmedAt = clock.UtcNow;
            order.CreditWarning = decision.Outcome == CreditOutcome.Allowed ? null : CreditControlService.WarningText(decision);
            order.Status = decision.Outcome == CreditOutcome.Hold ? SalesOrderStatus.CreditHold : SalesOrderStatus.Confirmed;
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public async Task<SalesOrder> ApproveCreditAsync(Guid id, CancellationToken ct)
    {
        var order = await FindAsync(id, ct);
        if (order.Status != SalesOrderStatus.CreditHold)
            throw new ConflictException("sal.so_not_held", "This order is not on credit hold.", "أمر البيع ليس موقوفاً بسبب حد الائتمان.");
        order.Status = SalesOrderStatus.Confirmed;
        order.CreditApprovedBy = tenant.ActingUserId;
        order.CreditApprovedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return order;
    }

    public Task<SalesOrder> RejectAsync(Guid id, CancellationToken ct) => TransitionAsync(id, SalesOrderStatus.Draft, ct, SalesOrderStatus.CreditHold);

    public Task<SalesOrder> CancelAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await FindAsync(id, token);
            if (order.Lines.Any(l => l.QuantityDelivered > 0 || l.QuantityInvoiced > 0))
                throw new ConflictException("sal.so_has_activity", "Orders with deliveries or invoices cannot be cancelled; close them instead.",
                    "لا يمكن إلغاء أمر بيع عليه تسليمات أو فواتير؛ قم بإغلاقه بدلاً من ذلك.");
            return await TransitionAsync(id, SalesOrderStatus.Cancelled, token, SalesOrderStatus.Draft, SalesOrderStatus.CreditHold, SalesOrderStatus.Confirmed);
        }, ct);

    public Task<SalesOrder> CloseAsync(Guid id, CancellationToken ct) => TransitionAsync(id, SalesOrderStatus.Closed, ct,
        SalesOrderStatus.Confirmed, SalesOrderStatus.PartiallyDelivered, SalesOrderStatus.Delivered);

    private async Task<SalesOrder> TransitionAsync(Guid id, SalesOrderStatus to, CancellationToken ct, params SalesOrderStatus[] from)
    {
        var order = await FindAsync(id, ct);
        if (!from.Contains(order.Status))
            throw new ConflictException("sal.so_status", $"A {order.Status} order cannot become {to}.", "لا يمكن تغيير حالة أمر البيع بهذا الشكل.");
        order.Status = to;
        await db.SaveChangesAsync(ct);
        return order;
    }

    private async Task<SalesOrder> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<SalesOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id && o.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Sales order", id);

    /// <summary>Delivered when every stock line is fully delivered; partially delivered once anything has left.</summary>
    internal static void RefreshDeliveryStatus(SalesOrder order, IReadOnlyDictionary<Guid, ItemInfo> items)
    {
        if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyDelivered or SalesOrderStatus.Delivered)) return;
        var stockLines = order.Lines.Where(l => items.TryGetValue(l.ItemId, out var i) && i.Type == ItemType.Stock).ToList();
        order.Status = stockLines.Count > 0 && stockLines.All(l => l.QuantityDelivered >= l.Quantity) ? SalesOrderStatus.Delivered
            : order.Lines.Any(l => l.QuantityDelivered > 0) ? SalesOrderStatus.PartiallyDelivered
            : SalesOrderStatus.Confirmed;
    }
}

/// <summary>Delivery notes take stock out at weighted-average cost: debit Cost of Sales, credit Inventory. Revenue follows on the invoice.</summary>
internal sealed class DeliveryNoteService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IStockService stock, IPostingService posting)
{
    public const string SourceType = "delivery_note";

    public Task<DeliveryNote> CreateAsync(DeliveryRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var order = await db.Set<SalesOrder>().Include(o => o.Lines)
                            .SingleOrDefaultAsync(o => o.Id == r.SalesOrderId && o.CompanyId == tenant.RequireCompanyId(), token)
                        ?? throw new NotFoundException("Sales order", r.SalesOrderId);
            if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyDelivered))
                throw new BusinessRuleException("sal.so_not_deliverable", "Goods can only be delivered against a confirmed, open order.",
                    "يمكن تسليم البضاعة فقط على أمر بيع مؤكد ومفتوح.");
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0))
                throw new BusinessRuleException("sal.delivery_lines", "Enter the quantities delivered.", "أدخل الكميات المسلّمة.");
            var items = await inventory.GetItemsAsync(order.Lines.Select(l => l.ItemId), token);
            var warehouseId = r.WarehouseId ?? order.WarehouseId;
            var customer = await db.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == order.CustomerId, token);

            var note = new DeliveryNote
            {
                CompanyId = order.CompanyId,
                DnNo = await numbering.NextAsync(SourceType, "DN", r.DeliveryDate, ct: token),
                SalesOrderId = order.Id,
                CustomerId = order.CustomerId,
                WarehouseId = warehouseId,
                DeliveryDate = r.DeliveryDate,
                DeliveryAddress = CustomerService.Clean(r.DeliveryAddress) ?? customer.AddressEn,
                VehicleNo = CustomerService.Clean(r.VehicleNo),
                Notes = CustomerService.Clean(r.Notes)
            };
            var lineNo = 0;
            foreach (var l in r.Lines)
            {
                var soLine = order.Lines.SingleOrDefault(x => x.Id == l.SalesOrderLineId) ?? throw new NotFoundException("Order line", l.SalesOrderLineId);
                if (items[soLine.ItemId].Type != ItemType.Stock)
                    throw new BusinessRuleException("sal.delivery_service", "Service lines are invoiced directly, not delivered.", "تُفوتر بنود الخدمات مباشرة ولا تُسلّم.");
                if (soLine.QuantityDelivered + l.Quantity > soLine.Quantity)
                    throw new BusinessRuleException("sal.over_delivery",
                        $"Line {soLine.LineNo}: only {soLine.Quantity - soLine.QuantityDelivered:0.##} left to deliver.",
                        $"السطر {soLine.LineNo}: المتبقي للتسليم {soLine.Quantity - soLine.QuantityDelivered:0.##} فقط.");
                soLine.QuantityDelivered += l.Quantity;
                note.Lines.Add(new DeliveryNoteLine
                {
                    DeliveryNoteId = note.Id, LineNo = ++lineNo, SalesOrderLineId = soLine.Id, ItemId = soLine.ItemId, Quantity = l.Quantity
                });
            }
            db.Add(note);

            var results = await stock.PostAsync(new StockPosting(SourceType, note.Id, note.DnNo, r.DeliveryDate,
                note.Lines.OrderBy(l => l.LineNo).Select((l, i) => new StockLine(l.ItemId, warehouseId, -l.Quantity, SourceLineId: l.Id,
                    Batches: Serials.AsBatches(r.Lines[i].Serials))).ToList()), token);
            var builder = new PostingBuilder();
            foreach (var x in results)
            {
                var line = note.Lines.Single(l => l.Id == x.SourceLineId);
                line.Cost = -x.Value;
                line.UnitCost = Math.Round(line.Cost / line.Quantity, 6, MidpointRounding.AwayFromZero);
                builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, -x.Value);
                builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.Value);
            }
            note.TotalCost = note.Lines.Sum(l => l.Cost);
            var lines = builder.Lines();
            if (lines.Count > 0)
                note.JournalEntryId = (await posting.PostAsync(new PostingRequest
                {
                    SourceType = SourceType, SourceId = note.Id, SourceNo = note.DnNo, PostingDate = r.DeliveryDate,
                    MemoEn = $"Delivery {note.DnNo} to {customer.NameEn} ({order.SoNo})", MemoAr = $"تسليم {note.DnNo} إلى {customer.NameAr} ({order.SoNo})",
                    Lines = lines
                }, token)).JournalEntryId;
            SalesOrderService.RefreshDeliveryStatus(order, items);
            await db.SaveChangesAsync(token);
            return note;
        }, ct);

    public Task<DeliveryNote> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var note = await db.Set<DeliveryNote>().Include(d => d.Lines)
                           .SingleOrDefaultAsync(d => d.Id == id && d.CompanyId == tenant.RequireCompanyId(), token)
                       ?? throw new NotFoundException("Delivery note", id);
            if (note.Status != DocumentStatus.Posted)
                throw new ConflictException("sal.not_posted", "This delivery note is already reversed.", "تم عكس إشعار التسليم مسبقاً.");
            if (note.Lines.Any(l => l.QuantityInvoiced > 0))
                throw new BusinessRuleException("sal.delivery_invoiced", "Reverse the invoice for this delivery first.", "قم بعكس فاتورة هذا التسليم أولاً.");
            await stock.ReverseAsync(SourceType, note.Id, r.Date, token);
            if (note.JournalEntryId is not null) await posting.ReverseAsync(SourceType, note.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var order = await db.Set<SalesOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == note.SalesOrderId, token);
            foreach (var l in note.Lines) order.Lines.Single(x => x.Id == l.SalesOrderLineId).QuantityDelivered -= l.Quantity;
            SalesOrderService.RefreshDeliveryStatus(order, await inventory.GetItemsAsync(order.Lines.Select(l => l.ItemId), token));
            note.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return note;
        }, ct);
}

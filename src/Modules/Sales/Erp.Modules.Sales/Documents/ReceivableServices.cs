using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

/// <summary>
/// An invoice line is one of: a delivery note line (goods already delivered), a service line of a sales order, an item
/// (stock items are issued by the invoice itself — a counter sale — and need the invoice's warehouse) or a free-text service.
/// Without a price, delivered and order lines take the order price and items the customer's price.
/// </summary>
/// <summary>For item lines, quantity and price are in <see cref="UomId"/> when given (an alternative unit), otherwise in the base unit.</summary>
public sealed record InvoiceLineRequest(Guid? DeliveryNoteLineId, Guid? SalesOrderLineId, Guid? ItemId, string? Description, decimal Quantity,
    decimal? UnitPrice = null, decimal? DiscountPercent = null, Guid? UomId = null);

/// <summary>Currency comes from the order (lines from orders) or the customer; a foreign currency needs today's rate to QAR.</summary>
public sealed record InvoiceRequest(Guid CustomerId, DateOnly InvoiceDate, DateOnly? DueDate, Guid? WarehouseId, string? CustomerRef, string? Notes,
    IReadOnlyList<InvoiceLineRequest> Lines, string? Currency = null, decimal? ExchangeRate = null);

public sealed record InvoiceDeliveryRequest(DateOnly InvoiceDate, string? CustomerRef, string? Notes, decimal? ExchangeRate = null);

public sealed record CreditNoteLineRequest(CreditNoteLineType LineType, Guid? SalesInvoiceLineId, string? Description, decimal Quantity, decimal? UnitPrice = null);

public sealed record CreditNoteRequest(Guid SalesInvoiceId, DateOnly Date, string Reason, Guid? WarehouseId, IReadOnlyList<CreditNoteLineRequest> Lines);

public sealed record ReceiptAllocationRequest(Guid SalesInvoiceId, decimal Amount);

/// <summary><see cref="OnAccount"/>: money received without (or beyond) an invoice; it stays as a customer credit.</summary>
/// <summary>Receipts for foreign-currency invoices are in that currency at today's <see cref="ExchangeRate"/>; amounts on account are QAR only.</summary>
public sealed record ReceiptRequest(Guid CustomerId, DateOnly Date, ReceiptMethod Method, Guid? DepositAccountId, string? Reference, string? ChequeNo,
    DateOnly? ChequeDate, string? ChequeBank, IReadOnlyList<ReceiptAllocationRequest> Allocations, decimal OnAccount = 0, decimal? ExchangeRate = null);

public sealed record PdcDepositRequest(DateOnly Date, Guid BankAccountId);

/// <summary>
/// Sales invoices. Posting debits Accounts Receivable and credits revenue by item group (services to service revenue);
/// counter-sale lines also issue stock at weighted-average cost (debit Cost of Sales, credit Inventory). Invoices that are
/// not covered by a confirmed order go through the credit check.
/// </summary>
internal sealed class SalesInvoiceService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IStockService stock, IPostingService posting, CustomerService customers, CreditControlService credit, IClock clock)
{
    public const string SourceType = "sales_invoice";

    public Task<SalesInvoice> CreateAsync(InvoiceRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var customer = await CustomerService.ActiveCustomerAsync(db, tenant, r.CustomerId, token);
            if (r.Lines is not { Count: > 0 })
                throw new BusinessRuleException("sal.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");

            var dnLineIds = r.Lines.Where(l => l.DeliveryNoteLineId is not null).Select(l => l.DeliveryNoteLineId!.Value).ToList();
            var dnLines = await (from l in db.Set<DeliveryNoteLine>()
                                 join d in db.Set<DeliveryNote>() on l.DeliveryNoteId equals d.Id
                                 join s in db.Set<SalesOrderLine>() on l.SalesOrderLineId equals s.Id
                                 join o in db.Set<SalesOrder>() on s.SalesOrderId equals o.Id
                                 where dnLineIds.Contains(l.Id) && d.CompanyId == customer.CompanyId
                                 select new { Line = l, d.CustomerId, d.Status, d.SalesOrderId, OrderLine = s, o.Currency }).ToDictionaryAsync(x => x.Line.Id, token);
            var soLineIds = r.Lines.Where(l => l.DeliveryNoteLineId is null && l.SalesOrderLineId is not null).Select(l => l.SalesOrderLineId!.Value).ToList();
            var soLines = await (from l in db.Set<SalesOrderLine>()
                                 join o in db.Set<SalesOrder>() on l.SalesOrderId equals o.Id
                                 where soLineIds.Contains(l.Id) && o.CompanyId == customer.CompanyId
                                 select new { Line = l, o.CustomerId, o.Status, o.Currency }).ToDictionaryAsync(x => x.Line.Id, token);
            var items = await inventory.GetItemsAsync(r.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value)
                .Concat(dnLines.Values.Select(x => x.Line.ItemId)).Concat(soLines.Values.Select(x => x.Line.ItemId)), token);
            var orderCurrencies = dnLines.Values.Select(x => x.Currency).Concat(soLines.Values.Select(x => x.Currency)).Distinct().ToList();
            if (orderCurrencies.Count > 1)
                throw new BusinessRuleException("sal.invoice_currencies", "Invoice orders in different currencies separately.", "افصل فواتير الأوامر ذات العملات المختلفة.");
            var currency = SalesRules.CurrencyCode(orderCurrencies.SingleOrDefault() ?? r.Currency ?? customer.Currency);
            if (r.Currency is not null && SalesRules.CurrencyCode(r.Currency) != currency)
                throw new BusinessRuleException("sal.invoice_currencies", $"These order lines are in {currency}.", $"بنود الأوامر هذه بعملة {currency}.");
            var rate = SalesRules.ExchangeRate(currency, r.ExchangeRate);
            var directIds = r.Lines.Where(l => l.DeliveryNoteLineId is null && l.SalesOrderLineId is null && l.ItemId is not null && l.UnitPrice is null)
                .Select(l => l.ItemId!.Value).ToList();
            var prices = directIds.Count > 0 ? await customers.PricesAsync(customer.Id, directIds, token, rate) : new Dictionary<Guid, decimal?>();
            var units = await inventory.GetUnitsAsync(r.Lines.Where(l => l.ItemId is not null && l.UomId is not null).Select(l => l.ItemId!.Value), token);

            var invoice = new SalesInvoice
            {
                CompanyId = customer.CompanyId,
                InvoiceNo = await numbering.NextAsync(SourceType, "SINV", r.InvoiceDate, ct: token),
                CustomerId = customer.Id,
                InvoiceDate = r.InvoiceDate,
                DueDate = r.DueDate ?? r.InvoiceDate.AddDays(customer.PaymentTermsDays),
                WarehouseId = r.WarehouseId,
                CustomerRef = CustomerService.Clean(r.CustomerRef),
                Notes = CustomerService.Clean(r.Notes),
                Currency = currency,
                ExchangeRate = rate
            };
            var orderIds = new HashSet<Guid>();
            var lineNo = 0;
            foreach (var l in r.Lines)
            {
                InvoiceLineType type;
                Guid? itemId = l.ItemId, soLineId = null, uomId = null;
                decimal price, discount, factor = 1m;
                decimal? unitCost = null;
                if (l.DeliveryNoteLineId is { } dnLineId)
                {
                    if (!dnLines.TryGetValue(dnLineId, out var dn)) throw new NotFoundException("Delivery note line", dnLineId);
                    if (dn.CustomerId != customer.Id || dn.Status != DocumentStatus.Posted)
                        throw new BusinessRuleException("sal.delivery_mismatch", "The delivery belongs to another customer or was reversed.",
                            "التسليم يخص عميلاً آخر أو تم عكسه.");
                    if (l.Quantity > dn.Line.Quantity - dn.Line.QuantityInvoiced)
                        throw new BusinessRuleException("sal.over_invoice", $"Only {dn.Line.Quantity - dn.Line.QuantityInvoiced:0.##} delivered and not yet invoiced.",
                            $"المسلّم وغير المفوتر {dn.Line.Quantity - dn.Line.QuantityInvoiced:0.##} فقط.");
                    type = InvoiceLineType.Delivered;
                    itemId = dn.Line.ItemId;
                    soLineId = dn.OrderLine.Id;
                    price = l.UnitPrice ?? dn.OrderLine.UnitPrice;
                    discount = l.DiscountPercent ?? dn.OrderLine.DiscountPercent;
                    unitCost = dn.Line.UnitCost;
                    orderIds.Add(dn.SalesOrderId);
                }
                else if (l.SalesOrderLineId is { } orderLineId)
                {
                    if (!soLines.TryGetValue(orderLineId, out var so) || so.CustomerId != customer.Id) throw new NotFoundException("Order line", orderLineId);
                    if (so.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyDelivered or SalesOrderStatus.Delivered))
                        throw new BusinessRuleException("sal.so_not_open", "The order is not confirmed or is already closed.", "أمر البيع غير مؤكد أو مغلق.");
                    if (items[so.Line.ItemId].Type == ItemType.Stock)
                        throw new BusinessRuleException("sal.deliver_first", "Stock lines of an order are invoiced from their delivery notes.",
                            "تُفوتر بنود المخزون في أمر البيع من خلال إشعارات التسليم.");
                    if (l.Quantity > so.Line.Quantity - so.Line.QuantityInvoiced)
                        throw new BusinessRuleException("sal.over_invoice", "More than ordered would be invoiced.", "الكمية المفوترة تتجاوز المطلوبة.");
                    type = InvoiceLineType.Service;
                    itemId = so.Line.ItemId;
                    soLineId = so.Line.Id;
                    price = l.UnitPrice ?? so.Line.UnitPrice;
                    discount = l.DiscountPercent ?? so.Line.DiscountPercent;
                    orderIds.Add(so.Line.SalesOrderId);
                }
                else if (itemId is { } id)
                {
                    if (!items.TryGetValue(id, out var item) || !item.IsActive) throw new NotFoundException("Item", id);
                    type = item.Type == ItemType.Stock ? InvoiceLineType.Direct : InvoiceLineType.Service;
                    if (type == InvoiceLineType.Direct && r.WarehouseId is null)
                        throw new BusinessRuleException("sal.invoice_warehouse", "Choose the warehouse the goods are taken from.", "اختر المستودع الذي تُصرف منه البضاعة.");
                    (uomId, factor) = ItemUnits.Resolve(units, item, l.UomId, "sal");
                    price = l.UnitPrice ?? prices.GetValueOrDefault(id) * factor
                            ?? throw new BusinessRuleException("sal.price_missing", $"Enter a price for {item.Sku}: it has no sales price.",
                                $"أدخل سعراً للصنف {item.Sku}: لا يوجد له سعر بيع.");
                    discount = l.DiscountPercent ?? 0;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(l.Description))
                        throw new BusinessRuleException("sal.line_description", "Describe the service, or choose an item.", "أدخل وصف الخدمة أو اختر صنفاً.");
                    type = InvoiceLineType.Service;
                    price = l.UnitPrice ?? throw new BusinessRuleException("sal.price_missing", "Enter the price of the service.", "أدخل سعر الخدمة.");
                    discount = l.DiscountPercent ?? 0;
                }

                var amount = SalesRules.LineAmount(l.Quantity, price, discount);
                invoice.Lines.Add(new SalesInvoiceLine
                {
                    SalesInvoiceId = invoice.Id, LineNo = ++lineNo, LineType = type, DeliveryNoteLineId = l.DeliveryNoteLineId, SalesOrderLineId = soLineId,
                    ItemId = itemId, Description = CustomerService.Clean(l.Description), Quantity = l.Quantity * factor,
                    UnitPrice = Math.Round(price / factor, 6, MidpointRounding.AwayFromZero), DiscountPercent = discount,
                    Amount = amount, UnitCost = unitCost, UomId = uomId, UomFactor = factor, UomQuantity = l.Quantity, UomPrice = price
                });
                invoice.DiscountTotal += SalesRules.LineDiscount(l.Quantity, price, amount);
            }
            invoice.Total = invoice.Lines.Sum(l => l.Amount);
            invoice.TotalBase = invoice.Lines.Sum(l => SalesRules.ToBase(l.Amount, rate));
            if (invoice.Total <= 0)
                throw new BusinessRuleException("sal.invoice_total", "The invoice total must be positive.", "يجب أن يكون إجمالي الفاتورة موجباً.");
            if (r.WarehouseId is { } wh && await inventory.GetWarehouseAsync(wh, token) is not { IsActive: true }) throw new NotFoundException("Warehouse", wh);
            if (orderIds.Count == 1) invoice.SalesOrderId = orderIds.Single();
            db.Add(invoice);
            await db.SaveChangesAsync(token);
            return invoice;
        }, ct);

    /// <summary>A draft invoice for everything on a delivery note that is not yet invoiced, at the order prices.</summary>
    public async Task<SalesInvoice> FromDeliveryAsync(Guid deliveryNoteId, InvoiceDeliveryRequest r, CancellationToken ct)
    {
        var note = await db.Set<DeliveryNote>().AsNoTracking().Include(d => d.Lines)
                       .SingleOrDefaultAsync(d => d.Id == deliveryNoteId && d.CompanyId == tenant.RequireCompanyId(), ct)
                   ?? throw new NotFoundException("Delivery note", deliveryNoteId);
        var open = note.Lines.Where(l => l.QuantityInvoiced < l.Quantity).OrderBy(l => l.LineNo).ToList();
        if (note.Status != DocumentStatus.Posted || open.Count == 0)
            throw new BusinessRuleException("sal.nothing_to_invoice", "Everything on this delivery note is already invoiced.", "تمت فوترة كل بنود إشعار التسليم.");
        var order = await db.Set<SalesOrder>().AsNoTracking().SingleAsync(o => o.Id == note.SalesOrderId, ct);
        return await CreateAsync(new InvoiceRequest(note.CustomerId, r.InvoiceDate, null, null, r.CustomerRef ?? order.CustomerRef, r.Notes,
            open.Select(l => new InvoiceLineRequest(l.Id, null, null, null, l.Quantity - l.QuantityInvoiced)).ToList(), order.Currency,
            r.ExchangeRate ?? order.ExchangeRate), ct);
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        var invoice = await FindAsync(id, ct);
        if (invoice.Status != InvoiceStatus.Draft)
            throw new ConflictException("sal.invoice_not_draft", "Only draft invoices can be deleted.", "يمكن حذف الفواتير المسودة فقط.");
        db.RemoveRange(invoice.Lines);
        db.Remove(invoice);
        await db.SaveChangesAsync(ct);
    }

    public Task<SalesInvoice> PostAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await FindAsync(id, token);
            if (invoice.Status != InvoiceStatus.Draft)
                throw new ConflictException("sal.invoice_not_draft", "This invoice is already posted.", "تم ترحيل هذه الفاتورة مسبقاً.");
            var customer = await CustomerService.ActiveCustomerAsync(db, tenant, invoice.CustomerId, token);
            var uncovered = SalesRules.ToBase(invoice.Lines.Where(l => l.SalesOrderLineId is null).Sum(l => l.Amount), invoice.ExchangeRate);
            if (uncovered > 0) await credit.EnsureAllowedAsync(customer, uncovered, token);

            var dnLines = await LinesAsync<DeliveryNoteLine>(invoice.Lines.Select(l => l.DeliveryNoteLineId), token);
            var soLines = await LinesAsync<SalesOrderLine>(invoice.Lines.Select(l => l.SalesOrderLineId), token);
            var items = await inventory.GetItemsAsync(invoice.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), token);
            var builder = new PostingBuilder();
            foreach (var l in invoice.Lines)
            {
                if (l.DeliveryNoteLineId is { } dnLineId)
                {
                    var dn = dnLines[dnLineId];
                    if (dn.QuantityInvoiced + l.Quantity > dn.Quantity)
                        throw new BusinessRuleException("sal.over_invoice", $"Line {l.LineNo}: the delivery has already been invoiced.",
                            $"السطر {l.LineNo}: تمت فوترة التسليم مسبقاً.");
                    dn.QuantityInvoiced += l.Quantity;
                    l.UnitCost = dn.UnitCost;
                    l.Cost = Money.Round(l.Quantity * dn.UnitCost);
                }
                if (l.SalesOrderLineId is { } soLineId)
                {
                    var so = soLines[soLineId];
                    if (so.QuantityInvoiced + l.Quantity > so.Quantity)
                        throw new BusinessRuleException("sal.over_invoice", $"Line {l.LineNo}: the order line has already been invoiced.",
                            $"السطر {l.LineNo}: تمت فوترة سطر الأمر مسبقاً.");
                    so.QuantityInvoiced += l.Quantity;
                }
                var group = l.ItemId is { } item ? items[item].ItemGroupId : (Guid?)null;
                builder.Add(l.LineType == InvoiceLineType.Service ? AccountRoles.ServiceRevenue : AccountRoles.SalesRevenue, group,
                    -SalesRules.ToBase(l.Amount, invoice.ExchangeRate));
            }

            var direct = invoice.Lines.Where(l => l.LineType == InvoiceLineType.Direct).ToList();
            if (direct.Count > 0)
            {
                var results = await stock.PostAsync(new StockPosting(SourceType, invoice.Id, invoice.InvoiceNo, invoice.InvoiceDate,
                    direct.Select(l => new StockLine(l.ItemId!.Value, invoice.WarehouseId!.Value, -l.Quantity, SourceLineId: l.Id)).ToList()), token);
                foreach (var x in results)
                {
                    var line = direct.Single(l => l.Id == x.SourceLineId);
                    line.Cost = -x.Value;
                    line.UnitCost = Math.Round(line.Cost / line.Quantity, 6, MidpointRounding.AwayFromZero);
                    builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, -x.Value);
                    builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.Value);
                }
            }
            invoice.TotalBase = invoice.Lines.Sum(l => SalesRules.ToBase(l.Amount, invoice.ExchangeRate));
            builder.Add(AccountRoles.AccountsReceivable, null, invoice.TotalBase, customer.Id);

            invoice.EInvoiceUuid = Guid.NewGuid();
            invoice.IssuedAt = clock.UtcNow;
            invoice.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = invoice.Id, SourceNo = invoice.InvoiceNo, PostingDate = invoice.InvoiceDate,
                MemoEn = $"Sales invoice {invoice.InvoiceNo} — {customer.NameEn}{FxMemo(invoice.Currency, invoice.Total, invoice.ExchangeRate)}",
                MemoAr = $"فاتورة مبيعات {invoice.InvoiceNo} — {customer.NameAr}{FxMemo(invoice.Currency, invoice.Total, invoice.ExchangeRate)}",
                Lines = builder.Lines()
            }, token)).JournalEntryId;
            invoice.Status = InvoiceStatus.Posted;
            await db.SaveChangesAsync(token);
            return invoice;
        }, ct);

    public Task<SalesInvoice> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await FindAsync(id, token);
            if (invoice.Status != InvoiceStatus.Posted || invoice.PaidAmount != 0 || invoice.CreditedAmount != 0)
                throw new ConflictException("sal.invoice_not_open", "Only posted invoices without receipts or credit notes can be reversed.",
                    "يمكن عكس الفواتير المرحّلة التي ليس عليها مقبوضات أو إشعارات دائنة فقط.");
            await stock.ReverseAsync(SourceType, invoice.Id, r.Date, token);
            await posting.ReverseAsync(SourceType, invoice.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var dnLines = await LinesAsync<DeliveryNoteLine>(invoice.Lines.Select(l => l.DeliveryNoteLineId), token);
            var soLines = await LinesAsync<SalesOrderLine>(invoice.Lines.Select(l => l.SalesOrderLineId), token);
            foreach (var l in invoice.Lines)
            {
                if (l.DeliveryNoteLineId is { } d) dnLines[d].QuantityInvoiced -= l.Quantity;
                if (l.SalesOrderLineId is { } s) soLines[s].QuantityInvoiced -= l.Quantity;
            }
            invoice.Status = InvoiceStatus.Reversed;
            await db.SaveChangesAsync(token);
            return invoice;
        }, ct);

    internal static string FxMemo(string currency, decimal amount, decimal rate) =>
        currency == Money.BaseCurrency ? "" : $" ({currency} {amount:N2} @ {rate:0.######})";

    internal static void RefreshPaymentStatus(SalesInvoice invoice) =>
        invoice.Status = invoice.PaidAmount + invoice.CreditedAmount >= invoice.Total ? InvoiceStatus.Paid
            : invoice.PaidAmount + invoice.CreditedAmount > 0 ? InvoiceStatus.PartiallyPaid
            : InvoiceStatus.Posted;

    private async Task<Dictionary<Guid, T>> LinesAsync<T>(IEnumerable<Guid?> ids, CancellationToken ct) where T : Entity
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? [] : await db.Set<T>().Where(l => list.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
    }

    private async Task<SalesInvoice> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<SalesInvoice>().Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id && i.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Sales invoice", id);
}

/// <summary>
/// Credit notes against a posted invoice. Returns bring the goods back into stock at the cost they left with (debit Inventory,
/// credit Cost of Sales); returns and allowances both debit Sales Returns and credit Accounts Receivable. A credit note can
/// take at most what is still open on the invoice.
/// </summary>
internal sealed class CreditNoteService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IStockService stock, IPostingService posting, CustomerCreditService credits)
{
    public const string SourceType = "credit_note";

    public Task<CreditNote> CreateAsync(CreditNoteRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var invoice = await db.Set<SalesInvoice>().Include(i => i.Lines)
                              .SingleOrDefaultAsync(i => i.Id == r.SalesInvoiceId && i.CompanyId == tenant.RequireCompanyId(), token)
                          ?? throw new NotFoundException("Sales invoice", r.SalesInvoiceId);
            if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid))
                throw new BusinessRuleException("sal.invoice_not_open", $"Invoice {invoice.InvoiceNo} cannot be credited.",
                    $"لا يمكن إصدار إشعار دائن على الفاتورة {invoice.InvoiceNo}.");
            if (r.Lines is not { Count: > 0 })
                throw new BusinessRuleException("sal.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");
            var items = await inventory.GetItemsAsync(invoice.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), token);

            var note = new CreditNote
            {
                CompanyId = invoice.CompanyId,
                Currency = invoice.Currency,
                ExchangeRate = invoice.ExchangeRate,
                CreditNoteNo = await numbering.NextAsync(SourceType, "SCN", r.Date, ct: token),
                CustomerId = invoice.CustomerId,
                SalesInvoiceId = invoice.Id,
                NoteDate = r.Date,
                WarehouseId = r.WarehouseId ?? invoice.WarehouseId ?? await DeliveryWarehouseAsync(invoice, token),
                Reason = Guard.Required(r.Reason, "reason"),
                EInvoiceUuid = Guid.NewGuid()
            };
            var builder = new PostingBuilder();
            var returns = new List<(CreditNoteLine Line, SalesInvoiceLine Original)>();
            var lineNo = 0;
            foreach (var l in r.Lines)
            {
                var original = l.SalesInvoiceLineId is { } lineId
                    ? invoice.Lines.SingleOrDefault(x => x.Id == lineId) ?? throw new NotFoundException("Invoice line", lineId)
                    : null;
                decimal price;
                if (l.LineType == CreditNoteLineType.Return)
                {
                    if (original is null || original.LineType == InvoiceLineType.Service)
                        throw new BusinessRuleException("sal.return_line", "Returns must refer to a goods line of the invoice.", "يجب ربط المرتجع بسطر بضاعة في الفاتورة.");
                    if (l.Quantity <= 0 || original.QuantityReturned + l.Quantity > original.Quantity)
                        throw new BusinessRuleException("sal.over_return", $"Only {original.Quantity - original.QuantityReturned:0.##} can still be returned.",
                            $"الكمية القابلة للإرجاع {original.Quantity - original.QuantityReturned:0.##} فقط.");
                    original.QuantityReturned += l.Quantity;
                    price = l.UnitPrice ?? Math.Round(original.Amount / original.Quantity, 6, MidpointRounding.AwayFromZero);
                }
                else
                {
                    if (original is null && string.IsNullOrWhiteSpace(l.Description))
                        throw new BusinessRuleException("sal.line_description", "Describe the allowance.", "أدخل وصف الخصم.");
                    price = l.UnitPrice ?? throw new BusinessRuleException("sal.price_missing", "Enter the allowance amount.", "أدخل مبلغ الخصم.");
                }
                var line = new CreditNoteLine
                {
                    CreditNoteId = note.Id, LineNo = ++lineNo, LineType = l.LineType, SalesInvoiceLineId = original?.Id, ItemId = original?.ItemId,
                    Description = CustomerService.Clean(l.Description) ?? original?.Description, Quantity = l.Quantity, UnitPrice = price,
                    Amount = SalesRules.LineAmount(l.Quantity, price, 0)
                };
                note.Lines.Add(line);
                builder.Add(AccountRoles.SalesReturns, original?.ItemId is { } item ? items[item].ItemGroupId : null, SalesRules.ToBase(line.Amount, invoice.ExchangeRate));
                if (l.LineType == CreditNoteLineType.Return) returns.Add((line, original!));
            }
            note.Total = note.Lines.Sum(l => l.Amount);
            var creditedBefore = await db.Set<CreditNote>().Where(n => n.SalesInvoiceId == invoice.Id && n.Status == DocumentStatus.Posted)
                .SumAsync(n => (decimal?)n.Total, token) ?? 0;
            if (creditedBefore + note.Total > invoice.Total)
                throw new BusinessRuleException("sal.credit_exceeds_invoice",
                    $"Invoice {invoice.InvoiceNo} totals {invoice.Total:N2} and {creditedBefore:N2} is already credited.",
                    $"إجمالي الفاتورة {invoice.InvoiceNo} هو {invoice.Total:N2} وتم إشعار {creditedBefore:N2} منها مسبقاً.");
            // What is still open is reduced; the rest of a credit on a paid invoice is owed back to the customer.
            note.AppliedAmount = Math.Min(note.Total, invoice.OpenAmount);
            note.UnappliedAmount = note.Total - note.AppliedAmount;
            if (note.UnappliedAmount > 0 && invoice.Currency != Money.BaseCurrency)
                throw new BusinessRuleException("sal.credit_exceeds_open", $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} {invoice.Currency} open.",
                    $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} {invoice.Currency} فقط.");
            note.AppliedBase = SalesRules.BaseCleared(note.AppliedAmount, invoice.OpenAmount, invoice.OpenBase, invoice.ExchangeRate);
            db.Add(note);

            if (returns.Count > 0)
            {
                if (note.WarehouseId is not { } warehouseId)
                    throw new BusinessRuleException("sal.return_warehouse", "Choose the warehouse the goods come back to.", "اختر المستودع الذي تعود إليه البضاعة.");
                var results = await stock.PostAsync(new StockPosting(SourceType, note.Id, note.CreditNoteNo, r.Date,
                    returns.Select(x => new StockLine(x.Original.ItemId!.Value, warehouseId, x.Line.Quantity, x.Original.UnitCost ?? 0, x.Line.Id)).ToList()), token);
                foreach (var x in results)
                {
                    var line = note.Lines.Single(l => l.Id == x.SourceLineId);
                    line.Cost = x.Value;
                    line.UnitCost = returns.Single(rt => rt.Line.Id == line.Id).Original.UnitCost;
                    builder.Add(AccountRoles.Inventory, x.ItemGroupId, x.Value);
                    builder.Add(AccountRoles.CostOfSales, x.ItemGroupId, -x.Value);
                }
                note.CostReturned = note.Lines.Sum(l => l.Cost);
            }
            // A credit that settles a foreign-currency invoice clears its remaining QAR; cents of rounding go to exchange differences.
            var linesBase = note.Lines.Sum(l => SalesRules.ToBase(l.Amount, invoice.ExchangeRate));
            var arBase = note.AppliedBase + note.UnappliedAmount;
            builder.Add(AccountRoles.AccountsReceivable, null, -arBase, invoice.CustomerId);
            if (arBase != linesBase) builder.Add(AccountRoles.ExchangeDifference, null, arBase - linesBase);

            var customer = await db.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == invoice.CustomerId, token);
            note.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = note.Id, SourceNo = note.CreditNoteNo, PostingDate = r.Date,
                MemoEn = $"Credit note {note.CreditNoteNo} on {invoice.InvoiceNo} — {customer.NameEn}",
                MemoAr = $"إشعار دائن {note.CreditNoteNo} على {invoice.InvoiceNo} — {customer.NameAr}",
                Lines = builder.Lines()
            }, token)).JournalEntryId;
            invoice.CreditedAmount += note.AppliedAmount;
            invoice.CreditedBase += note.AppliedBase;
            SalesInvoiceService.RefreshPaymentStatus(invoice);
            await db.SaveChangesAsync(token);
            return note;
        }, ct);

    public Task<CreditNote> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var note = await db.Set<CreditNote>().Include(n => n.Lines)
                           .SingleOrDefaultAsync(n => n.Id == id && n.CompanyId == tenant.RequireCompanyId(), token)
                       ?? throw new NotFoundException("Credit note", id);
            if (note.Status != DocumentStatus.Posted)
                throw new ConflictException("sal.not_posted", "This credit note is already reversed.", "تم عكس هذا الإشعار مسبقاً.");
            await credits.UnwindAsync(CreditSource.CreditNote, note.Id, token);
            await stock.ReverseAsync(SourceType, note.Id, r.Date, token);
            await posting.ReverseAsync(SourceType, note.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            var invoice = await db.Set<SalesInvoice>().Include(i => i.Lines).SingleAsync(i => i.Id == note.SalesInvoiceId, token);
            foreach (var l in note.Lines.Where(l => l.LineType == CreditNoteLineType.Return))
                invoice.Lines.Single(x => x.Id == l.SalesInvoiceLineId).QuantityReturned -= l.Quantity;
            invoice.CreditedAmount -= note.AppliedAmount;
            invoice.CreditedBase -= note.AppliedBase;
            SalesInvoiceService.RefreshPaymentStatus(invoice);
            note.UnappliedAmount = 0;
            note.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return note;
        }, ct);

    private async Task<Guid?> DeliveryWarehouseAsync(SalesInvoice invoice, CancellationToken ct)
    {
        var dnLineIds = invoice.Lines.Where(l => l.DeliveryNoteLineId is not null).Select(l => l.DeliveryNoteLineId!.Value).ToList();
        if (dnLineIds.Count == 0) return null;
        return await (from l in db.Set<DeliveryNoteLine>()
                      join d in db.Set<DeliveryNote>() on l.DeliveryNoteId equals d.Id
                      where dnLineIds.Contains(l.Id)
                      select (Guid?)d.WarehouseId).FirstOrDefaultAsync(ct);
    }
}

/// <summary>
/// Customer receipts allocated to open invoices: debit the bank or cash account, credit Accounts Receivable. A post-dated
/// cheque is debited to "PDC receivable" and held; depositing it on or after its date moves it to the bank, and a bounced
/// cheque reverses everything and reopens the invoices.
/// </summary>
internal sealed class CustomerReceiptService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting,
    IFinanceQueries finance, CustomerCreditService credits)
{
    public const string SourceType = "customer_receipt";
    public const string DepositSourceType = "pdc_deposit";

    public Task<CustomerReceipt> CreateAsync(ReceiptRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.CustomerId && c.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Customer", r.CustomerId);
            var pdc = r.Method == ReceiptMethod.PostDatedCheque;
            AccountSummary? account = null;
            if (pdc)
            {
                if (string.IsNullOrWhiteSpace(r.ChequeNo) || r.ChequeDate is null)
                    throw new BusinessRuleException("sal.cheque_details", "Enter the cheque number and date.", "أدخل رقم الشيك وتاريخه.");
                if (r.ChequeDate <= r.Date)
                    throw new BusinessRuleException("sal.pdc_date", "A post-dated cheque must be dated after the receipt; record it as a cheque instead.",
                        "يجب أن يكون تاريخ الشيك الآجل بعد تاريخ الاستلام؛ سجله كشيك عادي.");
            }
            else
            {
                account = r.DepositAccountId is { } accountId ? await finance.FindPostableAccountAsync(accountId, token) : null;
                if (account is null || account.Subtype is not ("bank" or "cash"))
                    throw new BusinessRuleException("sal.deposit_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");
                if (r.Method == ReceiptMethod.Cheque && string.IsNullOrWhiteSpace(r.ChequeNo))
                    throw new BusinessRuleException("sal.cheque_details", "Enter the cheque number.", "أدخل رقم الشيك.");
            }
            var allocations = r.Allocations ?? [];
            var onAccount = Money.Round(r.OnAccount);
            if (onAccount < 0 || (allocations.Count == 0 && onAccount == 0) || allocations.Any(a => a.Amount <= 0) ||
                allocations.Select(a => a.SalesInvoiceId).Distinct().Count() != allocations.Count)
                throw new BusinessRuleException("sal.receipt_allocations", "Allocate positive amounts to the invoices being paid (each once), or enter an amount on account.",
                    "وزّع مبالغ موجبة على الفواتير المسددة (كل فاتورة مرة واحدة)، أو أدخل مبلغاً تحت الحساب.");

            var ids = allocations.Select(a => a.SalesInvoiceId).ToList();
            var invoices = await db.Set<SalesInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            var currencies = invoices.Values.Select(i => i.Currency).Distinct().ToList();
            if (currencies.Count > 1)
                throw new BusinessRuleException("sal.receipt_currencies", "Receive payments for invoices in different currencies separately.",
                    "سجّل مقبوضات الفواتير ذات العملات المختلفة بشكل منفصل.");
            var currency = currencies.SingleOrDefault() ?? Money.BaseCurrency;
            if (currency != Money.BaseCurrency && onAccount > 0)
                throw new BusinessRuleException("sal.on_account_currency", "Amounts on account can only be received in QAR.", "يمكن استلام المبالغ تحت الحساب بالريال القطري فقط.");
            var receipt = new CustomerReceipt
            {
                CompanyId = customer.CompanyId,
                ReceiptNo = await numbering.NextAsync(SourceType, "RCPT", r.Date, ct: token),
                CustomerId = customer.Id,
                ReceiptDate = r.Date,
                Method = r.Method,
                DepositAccountId = account?.Id,
                Reference = CustomerService.Clean(r.Reference),
                ChequeNo = CustomerService.Clean(r.ChequeNo),
                ChequeDate = r.ChequeDate,
                ChequeBank = CustomerService.Clean(r.ChequeBank),
                Status = pdc ? ReceiptStatus.PdcHeld : ReceiptStatus.Posted,
                Currency = currency,
                ExchangeRate = SalesRules.ExchangeRate(currency, r.ExchangeRate)
            };
            foreach (var a in allocations)
            {
                if (!invoices.TryGetValue(a.SalesInvoiceId, out var invoice) || invoice.CustomerId != customer.Id)
                    throw new NotFoundException("Sales invoice", a.SalesInvoiceId);
                if (invoice.Status is not (InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid))
                    throw new BusinessRuleException("sal.invoice_not_open", $"Invoice {invoice.InvoiceNo} is not open for payment.",
                        $"الفاتورة {invoice.InvoiceNo} غير مفتوحة للسداد.");
                var amount = Money.Round(a.Amount);
                if (amount > invoice.OpenAmount)
                    throw new BusinessRuleException("sal.overpayment", $"Invoice {invoice.InvoiceNo} has only {invoice.OpenAmount:N2} open.",
                        $"المتبقي على الفاتورة {invoice.InvoiceNo} هو {invoice.OpenAmount:N2} فقط.");
                var cleared = SalesRules.BaseCleared(amount, invoice.OpenAmount, invoice.OpenBase, invoice.ExchangeRate);
                invoice.PaidAmount += amount;
                invoice.PaidBase += cleared;
                SalesInvoiceService.RefreshPaymentStatus(invoice);
                receipt.Allocations.Add(new CustomerReceiptAllocation
                {
                    CustomerReceiptId = receipt.Id, SalesInvoiceId = invoice.Id, Amount = amount, BaseCleared = cleared
                });
            }
            receipt.UnappliedAmount = onAccount;
            receipt.Amount = receipt.Allocations.Sum(a => a.Amount) + onAccount;
            receipt.AmountBase = SalesRules.ToBase(receipt.Amount, receipt.ExchangeRate);
            // The receivable is cleared at the invoices' rates and the money counted at today's; the difference is an exchange gain or loss.
            var clearedBase = receipt.Allocations.Sum(a => a.BaseCleared) + onAccount;
            receipt.FxDifference = receipt.AmountBase - clearedBase;
            db.Add(receipt);

            receipt.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = receipt.Id, SourceNo = receipt.ReceiptNo, PostingDate = r.Date,
                MemoEn = pdc ? $"Post-dated cheque {receipt.ChequeNo} from {customer.NameEn}" : $"Receipt from {customer.NameEn}",
                MemoAr = pdc ? $"شيك آجل {receipt.ChequeNo} من {customer.NameAr}" : $"مقبوضات من {customer.NameAr}",
                Lines = ReceiptLines(pdc, account, receipt, clearedBase, customer.Id)
            }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return receipt;
        }, ct);

    private static List<PostingLine> ReceiptLines(bool pdc, AccountSummary? account, CustomerReceipt receipt, decimal clearedBase, Guid customerId)
    {
        List<PostingLine> lines =
        [
            pdc
                ? new PostingLine { AccountRole = AccountRoles.PdcReceivable, Side = PostingSide.Debit, Amount = receipt.AmountBase }
                : new PostingLine { AccountId = account!.Id, Side = PostingSide.Debit, Amount = receipt.AmountBase },
            new PostingLine
            {
                AccountRole = AccountRoles.AccountsReceivable, Side = PostingSide.Credit, Amount = clearedBase, PartyType = PartyType.Customer, PartyId = customerId
            }
        ];
        if (receipt.FxDifference != 0)
            lines.Add(new PostingLine
            {
                AccountRole = AccountRoles.ExchangeDifference, Side = receipt.FxDifference > 0 ? PostingSide.Credit : PostingSide.Debit,
                Amount = Math.Abs(receipt.FxDifference), Description = "Exchange difference"
            });
        return lines;
    }

    /// <summary>Deposits a held cheque on or after its date: debit the bank, credit PDC receivable.</summary>
    public Task<CustomerReceipt> DepositAsync(Guid id, PdcDepositRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var receipt = await FindAsync(id, token);
            if (receipt.Status != ReceiptStatus.PdcHeld)
                throw new ConflictException("sal.pdc_not_held", "Only cheques held in hand can be deposited.", "يمكن إيداع الشيكات المحتفظ بها فقط.");
            if (r.Date < receipt.ChequeDate)
                throw new BusinessRuleException("sal.pdc_early", $"The cheque is dated {receipt.ChequeDate:dd/MM/yyyy} and cannot be deposited earlier.",
                    $"تاريخ الشيك {receipt.ChequeDate:dd/MM/yyyy} ولا يمكن إيداعه قبل ذلك.");
            var account = await finance.FindPostableAccountAsync(r.BankAccountId, token);
            if (account is null || account.Subtype != "bank")
                throw new BusinessRuleException("sal.deposit_account", "Choose an active bank account.", "اختر حساباً بنكياً نشطاً.");
            await posting.PostAsync(new PostingRequest
            {
                SourceType = DepositSourceType, SourceId = receipt.Id, SourceNo = receipt.ReceiptNo, PostingDate = r.Date,
                MemoEn = $"Cheque {receipt.ChequeNo} deposited", MemoAr = $"إيداع الشيك {receipt.ChequeNo}",
                Lines =
                [
                    new PostingLine { AccountId = account.Id, Side = PostingSide.Debit, Amount = receipt.AmountBase },
                    new PostingLine { AccountRole = AccountRoles.PdcReceivable, Side = PostingSide.Credit, Amount = receipt.AmountBase }
                ]
            }, token);
            receipt.Status = ReceiptStatus.PdcCleared;
            receipt.ClearedDate = r.Date;
            receipt.ClearedAccountId = account.Id;
            await db.SaveChangesAsync(token);
            return receipt;
        }, ct);

    /// <summary>A cheque returned unpaid (before or after depositing): the receipt and any deposit are reversed and the invoices reopen.</summary>
    public Task<CustomerReceipt> BounceAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        UndoAsync(id, r, ReceiptStatus.Bounced, [ReceiptStatus.PdcHeld, ReceiptStatus.PdcCleared], ct);

    /// <summary>Cancels a receipt entered by mistake (a held cheque returned to the customer counts too).</summary>
    public Task<CustomerReceipt> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        UndoAsync(id, r, ReceiptStatus.Reversed, [ReceiptStatus.Posted, ReceiptStatus.PdcHeld], ct);

    private Task<CustomerReceipt> UndoAsync(Guid id, ReverseRequest r, ReceiptStatus to, ReceiptStatus[] from, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var receipt = await FindAsync(id, token);
            if (!from.Contains(receipt.Status))
                throw new ConflictException("sal.receipt_status", $"A {receipt.Status} receipt cannot be {to.ToString().ToLowerInvariant()}.",
                    "لا يمكن تنفيذ هذا الإجراء على هذا السند.");
            var reason = Guard.Required(r.Reason, "reason");
            await credits.UnwindAsync(CreditSource.Receipt, receipt.Id, token);
            if (receipt.Status == ReceiptStatus.PdcCleared) await posting.ReverseAsync(DepositSourceType, receipt.Id, r.Date, reason, token);
            await posting.ReverseAsync(SourceType, receipt.Id, r.Date, reason, token);
            var ids = receipt.Allocations.Select(a => a.SalesInvoiceId).ToList();
            var invoices = await db.Set<SalesInvoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, token);
            foreach (var a in receipt.Allocations)
            {
                var invoice = invoices[a.SalesInvoiceId];
                invoice.PaidAmount -= a.Amount;
                invoice.PaidBase -= a.BaseCleared;
                SalesInvoiceService.RefreshPaymentStatus(invoice);
            }
            receipt.Status = to;
            receipt.StatusReason = reason;
            await db.SaveChangesAsync(token);
            return receipt;
        }, ct);

    private async Task<CustomerReceipt> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<CustomerReceipt>().Include(p => p.Allocations).SingleOrDefaultAsync(p => p.Id == id && p.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Customer receipt", id);
}

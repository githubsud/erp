using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Documents;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Purchasing.Sourcing;

public enum RfqStatus { Open, Awarded, Cancelled }

public enum BlanketStatus { Active, Closed }

public sealed class Rfq : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string RfqNo { get; init; }
    public DateOnly RfqDate { get; set; }
    public DateOnly? ReplyBy { get; set; }
    public Guid WarehouseId { get; set; }
    public RfqStatus Status { get; set; } = RfqStatus.Open;
    public string? Notes { get; set; }
    public List<RfqLine> Lines { get; init; } = [];
    public List<RfqSupplier> Suppliers { get; init; } = [];
}

public sealed class RfqLine : TenantEntity
{
    public Guid RfqId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
}

public sealed class RfqSupplier : TenantEntity
{
    public Guid RfqId { get; init; }
    public Guid SupplierId { get; init; }
    public Guid? PurchaseOrderId { get; set; }
}

public sealed class RfqQuote : TenantEntity
{
    public Guid RfqLineId { get; init; }
    public Guid SupplierId { get; init; }
    public decimal UnitPrice { get; set; }
    public int? LeadTimeDays { get; set; }
    public bool Awarded { get; set; }
}

public sealed class BlanketAgreement : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string AgreementNo { get; init; }
    public Guid SupplierId { get; init; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly ValidTo { get; set; }
    public BlanketStatus Status { get; set; } = BlanketStatus.Active;
    public string? Notes { get; set; }
    public List<BlanketLine> Lines { get; init; } = [];
}

public sealed class BlanketLine : TenantEntity
{
    public Guid AgreementId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal MaxQuantity { get; init; }
}

internal sealed class RfqConfiguration : IEntityTypeConfiguration<Rfq>
{
    public void Configure(EntityTypeBuilder<Rfq> b)
    {
        b.ToTable("rfq", "pur");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RfqId);
        b.HasMany(x => x.Suppliers).WithOne().HasForeignKey(x => x.RfqId);
    }
}

internal sealed class RfqLineConfiguration : IEntityTypeConfiguration<RfqLine>
{
    public void Configure(EntityTypeBuilder<RfqLine> b)
    {
        b.ToTable("rfq_line", "pur");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

internal sealed class RfqSupplierConfiguration : IEntityTypeConfiguration<RfqSupplier>
{
    public void Configure(EntityTypeBuilder<RfqSupplier> b) => b.ToTable("rfq_supplier", "pur");
}

internal sealed class RfqQuoteConfiguration : IEntityTypeConfiguration<RfqQuote>
{
    public void Configure(EntityTypeBuilder<RfqQuote> b)
    {
        b.ToTable("rfq_quote", "pur");
        b.Property(x => x.UnitPrice).AsQuantity();
    }
}

internal sealed class BlanketAgreementConfiguration : IEntityTypeConfiguration<BlanketAgreement>
{
    public void Configure(EntityTypeBuilder<BlanketAgreement> b)
    {
        b.ToTable("blanket_agreement", "pur");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.AgreementId);
    }
}

internal sealed class BlanketLineConfiguration : IEntityTypeConfiguration<BlanketLine>
{
    public void Configure(EntityTypeBuilder<BlanketLine> b)
    {
        b.ToTable("blanket_line", "pur");
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.MaxQuantity).AsQuantity();
    }
}

public sealed record RfqLineRequest(Guid ItemId, string? Description, decimal Quantity);

public sealed record RfqRequest(DateOnly RfqDate, DateOnly? ReplyBy, Guid WarehouseId, string? Notes, IReadOnlyList<RfqLineRequest> Lines,
    IReadOnlyList<Guid> SupplierIds);

public sealed record QuoteEntry(Guid RfqLineId, decimal? UnitPrice, int? LeadTimeDays);

public sealed record QuotesRequest(Guid SupplierId, IReadOnlyList<QuoteEntry> Quotes);

/// <summary>Either the whole RFQ to one supplier, or per line (<see cref="Lines"/>: RFQ line → supplier).</summary>
public sealed record AwardRequest(Guid? SupplierId, IReadOnlyDictionary<Guid, Guid>? Lines, DateOnly? OrderDate);

public sealed record ComparisonCell(Guid SupplierId, decimal? UnitPrice, decimal? Amount, int? LeadTimeDays, bool Lowest, bool Awarded);

public sealed record ComparisonLine(Guid RfqLineId, int LineNo, Guid ItemId, string Sku, string NameEn, string NameAr, string? Description, decimal Quantity,
    IReadOnlyList<ComparisonCell> Cells);

public sealed record ComparisonSupplier(Guid SupplierId, string Code, string NameEn, string NameAr, int QuotedLines, decimal Total, bool Complete,
    Guid? PurchaseOrderId, string? PoNo);

public sealed record RfqComparison(Guid Id, string RfqNo, DateOnly RfqDate, DateOnly? ReplyBy, Guid WarehouseId, string Status, string? Notes,
    IReadOnlyList<ComparisonSupplier> Suppliers, IReadOnlyList<ComparisonLine> Lines, decimal BestMixTotal, Guid? CheapestCompleteSupplierId);

public sealed record BlanketLineRequest(Guid ItemId, decimal UnitPrice, decimal MaxQuantity);

public sealed record BlanketRequest(Guid SupplierId, DateOnly ValidFrom, DateOnly ValidTo, string? Notes, IReadOnlyList<BlanketLineRequest> Lines);

public sealed record ReleaseLine(Guid ItemId, decimal Quantity);

public sealed record ReleaseRequest(DateOnly OrderDate, DateOnly? ExpectedDate, Guid WarehouseId, IReadOnlyList<ReleaseLine> Lines);

public sealed record BlanketLineDto(Guid Id, int LineNo, Guid ItemId, string Sku, string NameEn, string NameAr, decimal UnitPrice, decimal MaxQuantity,
    decimal Released, decimal Remaining);

public sealed record BlanketDto(Guid Id, string AgreementNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, DateOnly ValidFrom, DateOnly ValidTo,
    string Status, string? Notes, IReadOnlyList<BlanketLineDto> Lines, decimal AgreedValue, decimal ReleasedValue,
    IReadOnlyList<ReleaseOrderDto> Releases);

public sealed record ReleaseOrderDto(Guid Id, string PoNo, DateOnly OrderDate, string Status, decimal Total);

/// <summary>
/// Requests for quotation (several suppliers price the same lines; awarded per RFQ or per line into draft purchase orders) and
/// blanket agreements (agreed prices and maximum quantities for a period, released as purchase orders at those prices).
/// </summary>
internal sealed class SourcingService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory, IClock clock,
    PurchaseOrderService orders)
{
    public Task<Rfq> CreateRfqAsync(RfqRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0))
                throw new BusinessRuleException("pur.no_lines", "Add items with positive quantities.", "أضف أصنافاً بكميات موجبة.");
            if (r.SupplierIds.Distinct().Count() < 1)
                throw new BusinessRuleException("pur.rfq_suppliers", "Invite at least one supplier.", "ادعُ مورداً واحداً على الأقل.");
            if (await inventory.GetWarehouseAsync(r.WarehouseId, token) is not { IsActive: true }) throw new NotFoundException("Warehouse", r.WarehouseId);
            var items = await inventory.GetItemsAsync(r.Lines.Select(l => l.ItemId), token);
            foreach (var l in r.Lines)
                if (!items.TryGetValue(l.ItemId, out var i) || !i.IsActive) throw new NotFoundException("Item", l.ItemId);
            foreach (var s in r.SupplierIds.Distinct()) await PurchaseOrderService.ActiveSupplierAsync(db, tenant, s, token);
            var rfq = new Rfq
            {
                CompanyId = tenant.RequireCompanyId(), RfqNo = await numbering.NextAsync("rfq", "RFQ", r.RfqDate, ct: token), RfqDate = r.RfqDate,
                ReplyBy = r.ReplyBy, WarehouseId = r.WarehouseId, Notes = Clean(r.Notes)
            };
            var no = 0;
            foreach (var l in r.Lines)
                rfq.Lines.Add(new RfqLine { RfqId = rfq.Id, LineNo = ++no, ItemId = l.ItemId, Description = Clean(l.Description), Quantity = l.Quantity });
            foreach (var s in r.SupplierIds.Distinct()) rfq.Suppliers.Add(new RfqSupplier { RfqId = rfq.Id, SupplierId = s });
            db.Add(rfq);
            await db.SaveChangesAsync(token);
            return rfq;
        }, ct);

    /// <summary>Records (or corrects) one supplier's prices; a blank price means the supplier did not quote that line.</summary>
    public Task RecordQuotesAsync(Guid rfqId, QuotesRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var rfq = await FindRfqAsync(rfqId, token);
            if (rfq.Status != RfqStatus.Open) throw new ConflictException("pur.rfq_closed", "This RFQ is closed.", "طلب عرض الأسعار هذا مغلق.");
            if (rfq.Suppliers.All(s => s.SupplierId != r.SupplierId))
                throw new BusinessRuleException("pur.rfq_not_invited", "This supplier was not invited.", "هذا المورد غير مدعو.");
            var lineIds = rfq.Lines.Select(l => l.Id).ToList();
            var existing = await db.Set<RfqQuote>().Where(q => lineIds.Contains(q.RfqLineId) && q.SupplierId == r.SupplierId).ToDictionaryAsync(q => q.RfqLineId, token);
            foreach (var e in r.Quotes)
            {
                if (!lineIds.Contains(e.RfqLineId)) throw new NotFoundException("RFQ line", e.RfqLineId);
                if (e.UnitPrice is < 0) throw new BusinessRuleException("pur.quote_price", "Prices cannot be negative.", "لا يمكن أن تكون الأسعار سالبة.");
                existing.TryGetValue(e.RfqLineId, out var quote);
                if (e.UnitPrice is null)
                {
                    if (quote is not null) db.Remove(quote);
                    continue;
                }
                if (quote is null)
                    db.Add(new RfqQuote { RfqLineId = e.RfqLineId, SupplierId = r.SupplierId, UnitPrice = e.UnitPrice.Value, LeadTimeDays = e.LeadTimeDays });
                else
                {
                    quote.UnitPrice = e.UnitPrice.Value;
                    quote.LeadTimeDays = e.LeadTimeDays;
                }
            }
            await db.SaveChangesAsync(token);
            return 0;
        }, ct);

    public async Task<RfqComparison> CompareAsync(Guid rfqId, CancellationToken ct)
    {
        var rfq = await FindRfqAsync(rfqId, ct, tracking: false);
        var lineIds = rfq.Lines.Select(l => l.Id).ToList();
        var quotes = await db.Set<RfqQuote>().AsNoTracking().Where(q => lineIds.Contains(q.RfqLineId)).ToListAsync(ct);
        var supplierIds = rfq.Suppliers.Select(s => s.SupplierId).ToList();
        var suppliers = await db.Set<Supplier>().AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var poIds = rfq.Suppliers.Where(s => s.PurchaseOrderId != null).Select(s => s.PurchaseOrderId!.Value).ToList();
        var poNos = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => poIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.PoNo, ct);
        var items = await inventory.GetItemsAsync(rfq.Lines.Select(l => l.ItemId), ct);

        var lines = rfq.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var forLine = quotes.Where(q => q.RfqLineId == l.Id).ToList();
            var lowest = forLine.Count == 0 ? (decimal?)null : forLine.Min(q => q.UnitPrice);
            var cells = supplierIds.Select(s =>
            {
                var q = forLine.SingleOrDefault(x => x.SupplierId == s);
                return new ComparisonCell(s, q?.UnitPrice, q is null ? null : Money.Round(q.UnitPrice * l.Quantity), q?.LeadTimeDays,
                    q is not null && q.UnitPrice == lowest, q?.Awarded ?? false);
            }).ToList();
            return new ComparisonLine(l.Id, l.LineNo, l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn, items[l.ItemId].NameAr, l.Description, l.Quantity, cells);
        }).ToList();
        var summary = rfq.Suppliers.Select(s =>
        {
            var cells = lines.Select(l => l.Cells.Single(c => c.SupplierId == s.SupplierId)).Where(c => c.Amount != null).ToList();
            var supplier = suppliers[s.SupplierId];
            return new ComparisonSupplier(s.SupplierId, supplier.Code, supplier.NameEn, supplier.NameAr, cells.Count, cells.Sum(c => c.Amount!.Value),
                cells.Count == lines.Count, s.PurchaseOrderId, s.PurchaseOrderId is { } po ? poNos.GetValueOrDefault(po) : null);
        }).ToList();
        var bestMix = lines.Sum(l => l.Cells.Where(c => c.Amount != null).Select(c => c.Amount!.Value).DefaultIfEmpty(0).Min());
        var cheapest = summary.Where(s => s.Complete).OrderBy(s => s.Total).FirstOrDefault()?.SupplierId;
        return new RfqComparison(rfq.Id, rfq.RfqNo, rfq.RfqDate, rfq.ReplyBy, rfq.WarehouseId, rfq.Status.ToString(), rfq.Notes, summary, lines, bestMix, cheapest);
    }

    /// <summary>Creates one draft purchase order per awarded supplier at the quoted prices; the RFQ is then closed.</summary>
    public Task<List<Guid>> AwardAsync(Guid rfqId, AwardRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var rfq = await FindRfqAsync(rfqId, token);
            if (rfq.Status != RfqStatus.Open) throw new ConflictException("pur.rfq_closed", "This RFQ is closed.", "طلب عرض الأسعار هذا مغلق.");
            var lineIds = rfq.Lines.Select(l => l.Id).ToList();
            var quotes = await db.Set<RfqQuote>().Where(q => lineIds.Contains(q.RfqLineId)).ToListAsync(token);
            var awards = r.SupplierId is { } whole
                ? rfq.Lines.ToDictionary(l => l.Id, _ => whole)
                : (r.Lines ?? throw new BusinessRuleException("pur.rfq_award", "Choose a supplier.", "اختر مورداً.")).ToDictionary(x => x.Key, x => x.Value);
            if (awards.Count == 0) throw new BusinessRuleException("pur.rfq_award", "Choose a supplier.", "اختر مورداً.");
            var chosen = new List<(RfqLine Line, RfqQuote Quote)>();
            foreach (var (lineId, supplierId) in awards)
            {
                var line = rfq.Lines.SingleOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("RFQ line", lineId);
                var quote = quotes.SingleOrDefault(q => q.RfqLineId == lineId && q.SupplierId == supplierId)
                            ?? throw new BusinessRuleException("pur.rfq_no_quote", $"Line {line.LineNo}: the chosen supplier did not quote it.",
                                $"السطر {line.LineNo}: لم يقدم المورد المختار سعراً له.");
                quote.Awarded = true;
                chosen.Add((line, quote));
            }
            var created = new List<Guid>();
            foreach (var group in chosen.GroupBy(c => c.Quote.SupplierId))
            {
                var order = await orders.CreateAsync(new OrderRequest(group.Key, r.OrderDate ?? clock.Today,
                    group.Max(g => g.Quote.LeadTimeDays) is { } lead ? (r.OrderDate ?? clock.Today).AddDays(lead) : null, rfq.WarehouseId,
                    $"Awarded from {rfq.RfqNo}", group.OrderBy(g => g.Line.LineNo).Select(g => new OrderLineRequest(g.Line.ItemId, g.Line.Description,
                        g.Line.Quantity, g.Quote.UnitPrice)).ToList()), token);
                order.RfqId = rfq.Id;
                rfq.Suppliers.Single(s => s.SupplierId == group.Key).PurchaseOrderId = order.Id;
                created.Add(order.Id);
            }
            rfq.Status = RfqStatus.Awarded;
            await db.SaveChangesAsync(token);
            return created;
        }, ct);

    public async Task CancelRfqAsync(Guid rfqId, CancellationToken ct)
    {
        var rfq = await FindRfqAsync(rfqId, ct);
        if (rfq.Status != RfqStatus.Open) throw new ConflictException("pur.rfq_closed", "This RFQ is closed.", "طلب عرض الأسعار هذا مغلق.");
        rfq.Status = RfqStatus.Cancelled;
        await db.SaveChangesAsync(ct);
    }

    public Task<BlanketAgreement> CreateBlanketAsync(BlanketRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var supplier = await PurchaseOrderService.ActiveSupplierAsync(db, tenant, r.SupplierId, token);
            if (supplier.Currency != Money.BaseCurrency)
                throw new BusinessRuleException("pur.blanket_currency", "Blanket agreements are in QAR.", "الاتفاقيات الإطارية بالريال القطري.");
            if (r.ValidTo < r.ValidFrom) throw new BusinessRuleException("pur.blanket_dates", "Check the validity dates.", "تحقق من تواريخ السريان.");
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.MaxQuantity <= 0 || l.UnitPrice < 0) || r.Lines.Select(l => l.ItemId).Distinct().Count() != r.Lines.Count)
                throw new BusinessRuleException("pur.blanket_lines", "Add each item once with a price and a positive quantity.", "أضف كل صنف مرة واحدة بسعر وكمية موجبة.");
            var items = await inventory.GetItemsAsync(r.Lines.Select(l => l.ItemId), token);
            foreach (var l in r.Lines)
                if (!items.TryGetValue(l.ItemId, out var i) || !i.IsActive) throw new NotFoundException("Item", l.ItemId);
            var agreement = new BlanketAgreement
            {
                CompanyId = supplier.CompanyId, AgreementNo = await numbering.NextAsync("blanket_agreement", "BPA", r.ValidFrom, ct: token),
                SupplierId = supplier.Id, ValidFrom = r.ValidFrom, ValidTo = r.ValidTo, Notes = Clean(r.Notes)
            };
            var no = 0;
            foreach (var l in r.Lines)
                agreement.Lines.Add(new BlanketLine { AgreementId = agreement.Id, LineNo = ++no, ItemId = l.ItemId, UnitPrice = l.UnitPrice, MaxQuantity = l.MaxQuantity });
            db.Add(agreement);
            await db.SaveChangesAsync(token);
            return agreement;
        }, ct);

    /// <summary>A purchase order at the agreed prices, within the validity period and the quantities still open.</summary>
    public Task<PurchaseOrder> ReleaseAsync(Guid agreementId, ReleaseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var agreement = await FindBlanketAsync(agreementId, token);
            if (agreement.Status != BlanketStatus.Active || r.OrderDate < agreement.ValidFrom || r.OrderDate > agreement.ValidTo)
                throw new BusinessRuleException("pur.blanket_expired", "The agreement is not valid on this date.", "الاتفاقية غير سارية في هذا التاريخ.");
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0))
                throw new BusinessRuleException("pur.no_lines", "Add items with positive quantities.", "أضف أصنافاً بكميات موجبة.");
            var released = await ReleasedAsync(agreement.Id, token);
            var lines = new List<OrderLineRequest>();
            foreach (var l in r.Lines)
            {
                var line = agreement.Lines.SingleOrDefault(x => x.ItemId == l.ItemId)
                           ?? throw new BusinessRuleException("pur.blanket_item", "This item is not on the agreement.", "هذا الصنف غير مشمول بالاتفاقية.");
                var remaining = line.MaxQuantity - released.GetValueOrDefault(l.ItemId);
                if (l.Quantity > remaining)
                    throw new BusinessRuleException("pur.blanket_exceeded", $"Only {remaining:0.##} remain on the agreement for this item.",
                        $"المتبقي في الاتفاقية لهذا الصنف {remaining:0.##} فقط.");
                lines.Add(new OrderLineRequest(l.ItemId, null, l.Quantity, line.UnitPrice));
            }
            var order = await orders.CreateAsync(new OrderRequest(agreement.SupplierId, r.OrderDate, r.ExpectedDate, r.WarehouseId,
                $"Release against {agreement.AgreementNo}", lines), token);
            order.BlanketAgreementId = agreement.Id;
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public async Task<BlanketDto> GetBlanketAsync(Guid id, CancellationToken ct)
    {
        var a = await FindBlanketAsync(id, ct);
        var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == a.SupplierId, ct);
        var released = await ReleasedAsync(id, ct);
        var items = await inventory.GetItemsAsync(a.Lines.Select(l => l.ItemId), ct);
        var lines = a.Lines.OrderBy(l => l.LineNo).Select(l => new BlanketLineDto(l.Id, l.LineNo, l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn,
            items[l.ItemId].NameAr, l.UnitPrice, l.MaxQuantity, released.GetValueOrDefault(l.ItemId), l.MaxQuantity - released.GetValueOrDefault(l.ItemId))).ToList();
        var releases = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.BlanketAgreementId == id).OrderBy(o => o.OrderDate)
            .Select(o => new ReleaseOrderDto(o.Id, o.PoNo, o.OrderDate, o.Status.ToString(), o.Total)).ToListAsync(ct);
        return new BlanketDto(a.Id, a.AgreementNo, a.SupplierId, supplier.NameEn, supplier.NameAr, a.ValidFrom, a.ValidTo, a.Status.ToString(), a.Notes, lines,
            lines.Sum(l => Money.Round(l.UnitPrice * l.MaxQuantity)), lines.Sum(l => Money.Round(l.UnitPrice * l.Released)), releases);
    }

    public async Task CloseBlanketAsync(Guid id, CancellationToken ct)
    {
        var a = await FindBlanketAsync(id, ct);
        a.Status = BlanketStatus.Closed;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Quantities ordered against the agreement (cancelled orders give their quantity back).</summary>
    private async Task<Dictionary<Guid, decimal>> ReleasedAsync(Guid agreementId, CancellationToken ct) =>
        await (from l in db.Set<PurchaseOrderLine>().AsNoTracking()
               join o in db.Set<PurchaseOrder>() on l.PurchaseOrderId equals o.Id
               where o.BlanketAgreementId == agreementId && o.Status != PurchaseOrderStatus.Cancelled
               group l by l.ItemId into g
               select new { g.Key, Quantity = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);

    private async Task<Rfq> FindRfqAsync(Guid id, CancellationToken ct, bool tracking = true)
    {
        var q = db.Set<Rfq>().Include(r => r.Lines).Include(r => r.Suppliers).Where(r => r.Id == id && r.CompanyId == tenant.RequireCompanyId());
        return await (tracking ? q : q.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("RFQ", id);
    }

    private async Task<BlanketAgreement> FindBlanketAsync(Guid id, CancellationToken ct) =>
        await db.Set<BlanketAgreement>().Include(a => a.Lines).SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Blanket agreement", id);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

internal static class SourcingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing").WithTags("Sourcing").RequireAuthorization();
        g.MapGet("/rfqs", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await db.Set<Rfq>().AsNoTracking().Where(r => r.CompanyId == t.RequireCompanyId()).OrderByDescending(r => r.RfqDate).ThenByDescending(r => r.RfqNo)
                .Select(r => new { r.Id, r.RfqNo, r.RfqDate, r.ReplyBy, Status = r.Status.ToString(), Lines = r.Lines.Count, Suppliers = r.Suppliers.Count })
                .Take(200).ToListAsync(ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/rfqs/{id:guid}", (Guid id, SourcingService s, CancellationToken ct) => s.CompareAsync(id, ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/rfqs", async (RfqRequest r, SourcingService s, CancellationToken ct) => await s.CompareAsync((await s.CreateRfqAsync(r, ct)).Id, ct))
            .RequirePermission(PurchasingPermissions.OrdersManage);
        g.MapPut("/rfqs/{id:guid}/quotes", async (Guid id, QuotesRequest r, SourcingService s, CancellationToken ct) =>
        {
            await s.RecordQuotesAsync(id, r, ct);
            return await s.CompareAsync(id, ct);
        }).RequirePermission(PurchasingPermissions.OrdersManage);
        g.MapPost("/rfqs/{id:guid}/award", async (Guid id, AwardRequest r, SourcingService s, CancellationToken ct) =>
            new { PurchaseOrderIds = await s.AwardAsync(id, r, ct) }).RequirePermission(PurchasingPermissions.OrdersManage);
        g.MapPost("/rfqs/{id:guid}/cancel", async (Guid id, SourcingService s, CancellationToken ct) =>
        {
            await s.CancelRfqAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(PurchasingPermissions.OrdersManage);

        g.MapGet("/blanket-agreements", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await (from a in db.Set<BlanketAgreement>().AsNoTracking()
                   join s in db.Set<Supplier>() on a.SupplierId equals s.Id
                   where a.CompanyId == t.RequireCompanyId()
                   orderby a.ValidTo descending
                   select new { a.Id, a.AgreementNo, SupplierNameEn = s.NameEn, SupplierNameAr = s.NameAr, a.ValidFrom, a.ValidTo, Status = a.Status.ToString() })
                .ToListAsync(ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/blanket-agreements/{id:guid}", (Guid id, SourcingService s, CancellationToken ct) => s.GetBlanketAsync(id, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/blanket-agreements", async (BlanketRequest r, SourcingService s, CancellationToken ct) =>
            await s.GetBlanketAsync((await s.CreateBlanketAsync(r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.OrdersManage);
        g.MapPost("/blanket-agreements/{id:guid}/release", async (Guid id, ReleaseRequest r, SourcingService s, CancellationToken ct) =>
        {
            var order = await s.ReleaseAsync(id, r, ct);
            return new { PurchaseOrderId = order.Id, order.PoNo, order.Total };
        }).RequirePermission(PurchasingPermissions.OrdersManage);
        g.MapPost("/blanket-agreements/{id:guid}/close", async (Guid id, SourcingService s, CancellationToken ct) =>
        {
            await s.CloseBlanketAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(PurchasingPermissions.OrdersManage);
    }
}

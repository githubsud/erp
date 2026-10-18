using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Documents;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Endpoints;

public sealed record SupplierDto(Guid Id, string Code, string NameEn, string NameAr, string Country, bool IsResident, bool HasPermanentEstablishment,
    bool SubjectToWht, string? TaxCardNo, string? CrNo, string? Email, string? Phone, int PaymentTermsDays, string DefaultWhtCategory, bool IsActive,
    string Currency);

public sealed record PurchaseSettingsDto(decimal PriceTolerancePercent);
public sealed record DecisionRequest(string? Comment);

public sealed record OrderLineDto(Guid Id, int LineNo, Guid ItemId, string Sku, string NameEn, string NameAr, string ItemType, string UomCode,
    string? Description, decimal Quantity, decimal UnitPrice, decimal Amount, decimal QuantityReceived, decimal QuantityInvoiced, string WhtCategory,
    Guid? UomId, string? OrderUomCode, decimal UomFactor, decimal UomQuantity, decimal UomPrice);

public sealed record OrderDto(Guid Id, string PoNo, Guid SupplierId, string SupplierCode, string SupplierNameEn, string SupplierNameAr, DateOnly OrderDate,
    DateOnly? ExpectedDate, Guid WarehouseId, string Status, decimal Total, string? Notes, bool RequiresApproval, DateTimeOffset? ApprovedAt,
    DateOnly? GtaNotificationDue, string? GtaNotificationRef, IReadOnlyList<OrderLineDto> Lines, string Currency, decimal ExchangeRate);

public sealed record ReceiptLineDto(Guid Id, int LineNo, Guid PurchaseOrderLineId, Guid ItemId, string Sku, string NameEn, string NameAr, decimal Quantity,
    decimal UnitCost, decimal Value, decimal QuantityInvoiced);

public sealed record ReceiptDto(Guid Id, string GrnNo, Guid PurchaseOrderId, string PoNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr,
    Guid WarehouseId, DateOnly ReceiptDate, string? SupplierDeliveryNote, string Status, decimal TotalValue, Guid? JournalEntryId,
    IReadOnlyList<ReceiptLineDto> Lines, decimal ExchangeRate);

public sealed record InvoiceLineDto(Guid Id, int LineNo, string LineType, Guid? GoodsReceiptLineId, Guid? PurchaseOrderLineId, Guid? ItemId, string? Sku,
    string? Description, decimal Quantity, decimal UnitPrice, decimal Amount, decimal? ReceiptUnitCost, decimal Variance, string WhtCategory,
    decimal WhtRate, decimal WhtAmount, decimal QuantityReturned);

public sealed record InvoiceDto(Guid Id, string InvoiceNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, string SupplierRef,
    DateOnly InvoiceDate, DateOnly DueDate, Guid? PurchaseOrderId, decimal Total, decimal WhtAmount, decimal PaidAmount, decimal OpenAmount,
    string Status, string MatchStatus, string? Notes, Guid? JournalEntryId, IReadOnlyList<InvoiceLineDto> Lines, bool IsOpening, decimal DebitedAmount,
    string Currency, decimal ExchangeRate, decimal TotalBase, decimal OpenBase);

public sealed record DebitNoteLineDto(int LineNo, string LineType, Guid? SupplierInvoiceLineId, Guid? ItemId, string? Sku, string? Description,
    decimal Quantity, decimal UnitPrice, decimal Amount, decimal StockValue);

public sealed record DebitNoteDto(Guid Id, string DebitNoteNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, Guid SupplierInvoiceId,
    string InvoiceNo, string SupplierRef, DateOnly NoteDate, Guid? WarehouseId, string Reason, decimal Total, string Status, Guid? JournalEntryId,
    IReadOnlyList<DebitNoteLineDto> Lines);

public sealed record ChargeDto(int LineNo, string ChargeType, string? Description, decimal Amount, Guid? SupplierInvoiceId);

public sealed record AllocationDto(Guid GoodsReceiptLineId, Guid ItemId, string Sku, decimal Basis, decimal Amount, decimal ToInventory, decimal ToCostOfSales);

public sealed record LandedCostDto(Guid Id, string VoucherNo, DateOnly VoucherDate, string Method, string Status, decimal TotalAmount, string? Notes,
    Guid? JournalEntryId, IReadOnlyList<ChargeDto> Charges, IReadOnlyList<AllocationDto> Allocations);

public sealed record PaymentAllocationDto(Guid SupplierInvoiceId, string InvoiceNo, string SupplierRef, decimal Amount, decimal WhtAmount);

public sealed record PaymentDto(Guid Id, string PaymentNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, DateOnly PaymentDate,
    Guid PaymentAccountId, string? Reference, decimal GrossAmount, decimal WhtAmount, decimal NetAmount, string Status, Guid? JournalEntryId,
    IReadOnlyList<PaymentAllocationDto> Allocations, string Currency, decimal ExchangeRate, decimal FxDifference);

public sealed record GrniRow(Guid GoodsReceiptId, string GrnNo, DateOnly ReceiptDate, string SupplierNameEn, string SupplierNameAr, string Sku,
    decimal QuantityNotInvoiced, decimal UnitCost, decimal Value);

public sealed record GrniReport(IReadOnlyList<GrniRow> Rows, decimal Total, decimal LedgerBalance, decimal Difference);

public sealed record AgingRow(Guid SupplierId, string Code, string NameEn, string NameAr, decimal Current, decimal Days1To30, decimal Days31To60,
    decimal Days61To90, decimal Over90, decimal Total);

public sealed record AgingReport(DateOnly AsOf, IReadOnlyList<AgingRow> Rows, decimal Total, decimal LedgerBalance);

public sealed record WhtRow(DateOnly PaymentDate, string PaymentNo, string SupplierNameEn, string SupplierNameAr, string Country, string? TaxCardNo,
    string InvoiceNo, string Categories, decimal Gross, decimal Rate, decimal Tax);

public sealed record WhtReport(int Year, int Month, DateOnly FilingDeadline, IReadOnlyList<WhtRow> Rows, decimal TotalGross, decimal TotalTax);

public sealed record OpeningInvoiceResult(Guid Id, string InvoiceNo, Guid PartyId, string Reference, DateOnly InvoiceDate, DateOnly DueDate,
    decimal Amount);

internal static class PurchasingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing").WithTags("Purchasing").RequireAuthorization();

        g.MapGet("/settings", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            var s = await db.Set<PurchaseSettings>().AsNoTracking().SingleAsync(x => x.CompanyId == t.RequireCompanyId(), ct);
            return new PurchaseSettingsDto(s.PriceTolerancePercent);
        }).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPut("/settings", async (PurchaseSettingsRequest r, SupplierService s, CancellationToken ct) =>
        {
            var x = await s.SaveSettingsAsync(r, ct);
            return new PurchaseSettingsDto(x.PriceTolerancePercent);
        }).RequirePermission(PurchasingPermissions.SettingsManage);

        g.MapGet("/suppliers", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, string? search = null) =>
        {
            var q = db.Set<Supplier>().AsNoTracking().Where(s => s.CompanyId == t.RequireCompanyId());
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                q = q.Where(s => EF.Functions.ILike(s.Code, $"%{term}%") || EF.Functions.ILike(s.NameEn, $"%{term}%") || s.NameAr.Contains(term));
            }
            return (await q.OrderBy(s => s.Code).Take(1000).ToListAsync(ct)).Select(ToDto);
        }).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/suppliers/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            ToDto(await db.Set<Supplier>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == t.RequireCompanyId(), ct)
                  ?? throw new NotFoundException("Supplier", id))).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/suppliers", async (SupplierRequest r, SupplierService s, CancellationToken ct) => ToDto(await s.SaveAsync(null, r, ct)))
            .RequirePermission(PurchasingPermissions.SuppliersManage);
        g.MapPut("/suppliers/{id:guid}", async (Guid id, SupplierRequest r, SupplierService s, CancellationToken ct) => ToDto(await s.SaveAsync(id, r, ct)))
            .RequirePermission(PurchasingPermissions.SuppliersManage);
        g.MapGet("/suppliers/{id:guid}/balance", async (Guid id, IFinanceQueries f, CancellationToken ct, DateOnly? asOf = null) =>
            new { supplierId = id, balance = -await f.GetPartyBalanceAsync(AccountRoles.AccountsPayable, PartyType.Supplier, id,
                asOf ?? DateOnly.FromDateTime(DateTime.UtcNow), ct) }).RequirePermission(PurchasingPermissions.SuppliersRead);

        g.MapGet("/orders", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, string? status = null, Guid? supplierId = null) =>
            await OrdersAsync(db, t, inv, null, status, supplierId, ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/orders/{id:guid}", (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) => OrderAsync(db, t, inv, id, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/orders", async (OrderRequest r, PurchaseOrderService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            await OrderAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.OrdersManage);
        g.MapPut("/orders/{id:guid}", async (Guid id, OrderRequest r, PurchaseOrderService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) => await OrderAsync(db, t, inv, (await s.UpdateAsync(id, r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.OrdersManage);
        MapOrderAction(g, "submit", (s, id, ct) => s.SubmitAsync(id, ct), PurchasingPermissions.OrdersManage);
        g.MapPost("/orders/{id:guid}/approve", async (Guid id, DecisionRequest? r, PurchaseOrderService s, ErpDbContext db, ITenantContext t,
            IInventoryQueries inv, CancellationToken ct) => await OrderAsync(db, t, inv, (await s.DecideAsync(id, true, r?.Comment, ct)).Id, ct))
            .RequirePermission(PurchasingPermissions.OrdersApprove);
        g.MapPost("/orders/{id:guid}/reject", async (Guid id, DecisionRequest? r, PurchaseOrderService s, ErpDbContext db, ITenantContext t,
            IInventoryQueries inv, CancellationToken ct) => await OrderAsync(db, t, inv, (await s.DecideAsync(id, false, r?.Comment, ct)).Id, ct))
            .RequirePermission(PurchasingPermissions.OrdersApprove);
        MapOrderAction(g, "cancel", (s, id, ct) => s.CancelAsync(id, ct), PurchasingPermissions.OrdersManage);
        MapOrderAction(g, "close", (s, id, ct) => s.CloseAsync(id, ct), PurchasingPermissions.OrdersManage);
        g.MapPut("/orders/{id:guid}/gta-notification", async (Guid id, GtaNotificationRequest r, PurchaseOrderService s, ErpDbContext db, ITenantContext t,
            IInventoryQueries inv, CancellationToken ct) => await OrderAsync(db, t, inv, (await s.SetGtaNotificationAsync(id, r, ct)).Id, ct))
            .RequirePermission(PurchasingPermissions.OrdersManage);

        g.MapGet("/receipts", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? purchaseOrderId = null, Guid? supplierId = null) =>
            await ReceiptsAsync(db, t, inv, null, purchaseOrderId, supplierId, ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/receipts/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, inv, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Goods receipt", id))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/receipts", async (ReceiptRequest r, GoodsReceiptService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, null, null, ct)).Single()).RequirePermission(PurchasingPermissions.ReceiptsPost);
        g.MapPost("/receipts/{id:guid}/reverse", async (Guid id, ReverseRequest r, GoodsReceiptService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await ReceiptsAsync(db, t, inv, id, null, null, ct)).Single();
        }).RequirePermission(PurchasingPermissions.ReceiptsPost);

        g.MapGet("/invoices", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? supplierId = null, string? status = null,
            bool openOnly = false, bool? opening = null) => await InvoicesAsync(db, t, inv, null, supplierId, status, openOnly, ct, opening))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/opening-balances", async (SupplierOpeningRequest r, SupplierOpeningService s, CancellationToken ct) =>
            (await s.CreateAsync(r, ct)).Select(i => new OpeningInvoiceResult(i.Id, i.InvoiceNo, i.SupplierId, i.SupplierRef, i.InvoiceDate, i.DueDate,
                i.Total)).ToList()).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapGet("/invoices/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await InvoicesAsync(db, t, inv, id, null, null, false, ct)).SingleOrDefault() ?? throw new NotFoundException("Supplier invoice", id))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/invoices", async (InvoiceRequest r, SupplierInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await InvoicesAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, null, null, false, ct)).Single()).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapDelete("/invoices/{id:guid}", async (Guid id, SupplierInvoiceService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("/invoices/{id:guid}/approve-variance", async (Guid id, SupplierInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            await s.ApproveVarianceAsync(id, ct);
            return (await InvoicesAsync(db, t, inv, id, null, null, false, ct)).Single();
        }).RequirePermission(PurchasingPermissions.InvoicesApprove);
        g.MapPost("/invoices/{id:guid}/post", async (Guid id, SupplierInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            await s.PostAsync(id, ct);
            return (await InvoicesAsync(db, t, inv, id, null, null, false, ct)).Single();
        }).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("/invoices/{id:guid}/reverse", async (Guid id, ReverseRequest r, SupplierInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await InvoicesAsync(db, t, inv, id, null, null, false, ct)).Single();
        }).RequirePermission(PurchasingPermissions.InvoicesPost);

        g.MapGet("/debit-notes", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? supplierId = null,
            Guid? supplierInvoiceId = null) => await DebitNotesAsync(db, t, inv, null, supplierId, supplierInvoiceId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/debit-notes", async (DebitNoteRequest r, DebitNoteService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await DebitNotesAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, null, null, ct)).Single()).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("/debit-notes/{id:guid}/reverse", async (Guid id, ReverseRequest r, DebitNoteService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await DebitNotesAsync(db, t, inv, id, null, null, ct)).Single();
        }).RequirePermission(PurchasingPermissions.InvoicesPost);

        g.MapGet("/landed-costs", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) => await LandedCostsAsync(db, t, inv, null, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/landed-costs/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await LandedCostsAsync(db, t, inv, id, ct)).SingleOrDefault() ?? throw new NotFoundException("Landed cost voucher", id))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/landed-costs", async (LandedCostRequest r, LandedCostService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await LandedCostsAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, ct)).Single()).RequirePermission(PurchasingPermissions.InvoicesPost);
        g.MapPost("/landed-costs/{id:guid}/reverse", async (Guid id, ReverseRequest r, LandedCostService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await LandedCostsAsync(db, t, inv, id, ct)).Single();
        }).RequirePermission(PurchasingPermissions.InvoicesPost);

        g.MapGet("/payments", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? supplierId = null) =>
            await PaymentsAsync(db, t, null, supplierId, ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/payments/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await PaymentsAsync(db, t, id, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Supplier payment", id))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/payments", async (PaymentRequest r, SupplierPaymentService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await PaymentsAsync(db, t, (await s.CreateAsync(r, ct)).Id, null, ct)).Single()).RequirePermission(PurchasingPermissions.PaymentsPost);
        g.MapPost("/payments/{id:guid}/reverse", async (Guid id, ReverseRequest r, SupplierPaymentService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await PaymentsAsync(db, t, id, null, ct)).Single();
        }).RequirePermission(PurchasingPermissions.PaymentsPost);

        g.MapGet("/reports/grni", GrniAsync).RequirePermission(PurchasingPermissions.ReportsRead);
        g.MapGet("/reports/aging", AgingAsync).RequirePermission(PurchasingPermissions.ReportsRead);
        g.MapGet("/reports/wht", WhtAsync).RequirePermission(PurchasingPermissions.ReportsRead);
    }

    private static void MapOrderAction(RouteGroupBuilder g, string action, Func<PurchaseOrderService, Guid, CancellationToken, Task<PurchaseOrder>> run,
        string permission) =>
        g.MapPost($"/orders/{{id:guid}}/{action}", async (Guid id, PurchaseOrderService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) => await OrderAsync(db, t, inv, (await run(s, id, ct)).Id, ct)).RequirePermission(permission);

    private static SupplierDto ToDto(Supplier s) => new(s.Id, s.Code, s.NameEn, s.NameAr, s.Country, s.IsResident, s.HasPermanentEstablishment,
        s.SubjectToWht, s.TaxCardNo, s.CrNo, s.Email, s.Phone, s.PaymentTermsDays, s.DefaultWhtCategory.ToString(), s.IsActive, s.Currency);

    private static async Task<OrderDto> OrderAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid id, CancellationToken ct) =>
        (await OrdersAsync(db, t, inv, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Purchase order", id);

    private static async Task<List<OrderDto>> OrdersAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, string? status, Guid? supplierId,
        CancellationToken ct)
    {
        var q = db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).Where(o => o.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(o => o.Id == id);
        if (Enum.TryParse<PurchaseOrderStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
        if (supplierId is not null) q = q.Where(o => o.SupplierId == supplierId);
        var orders = await q.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.PoNo).Take(500).ToListAsync(ct);
        var suppliers = await SuppliersAsync(db, orders.Select(o => o.SupplierId), ct);
        var items = await inv.GetItemsAsync(orders.SelectMany(o => o.Lines.Select(l => l.ItemId)), ct);
        var uomIds = orders.SelectMany(o => o.Lines.Where(l => l.UomId != null).Select(l => l.UomId!.Value)).Distinct().ToList();
        var uomCodes = (await inv.GetUnitsAsync(orders.SelectMany(o => o.Lines.Where(l => l.UomId != null).Select(l => l.ItemId)), ct))
            .Where(u => uomIds.Contains(u.UomId)).GroupBy(u => u.UomId).ToDictionary(g => g.Key, g => g.First().UomCode);
        return orders.Select(o => new OrderDto(o.Id, o.PoNo, o.SupplierId, suppliers[o.SupplierId].Code, suppliers[o.SupplierId].NameEn,
            suppliers[o.SupplierId].NameAr, o.OrderDate, o.ExpectedDate, o.WarehouseId, o.Status.ToString(), o.Total, o.Notes, o.RequiresApproval, o.ApprovedAt,
            o.GtaNotificationDue, o.GtaNotificationRef,
            o.Lines.OrderBy(l => l.LineNo).Select(l => new OrderLineDto(l.Id, l.LineNo, l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn,
                items[l.ItemId].NameAr, items[l.ItemId].Type.ToString(), items[l.ItemId].UomCode, l.Description, l.Quantity, l.UnitPrice, l.Amount,
                l.QuantityReceived, l.QuantityInvoiced, l.WhtCategory.ToString(), l.UomId, l.UomId is { } u ? uomCodes.GetValueOrDefault(u) : null, l.UomFactor,
                l.UomQuantity, l.UomPrice)).ToList(), o.Currency, o.ExchangeRate)).ToList();
    }

    private static async Task<List<ReceiptDto>> ReceiptsAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, Guid? orderId, Guid? supplierId, CancellationToken ct)
    {
        var q = db.Set<GoodsReceipt>().AsNoTracking().Include(r => r.Lines).Where(r => r.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(r => r.Id == id);
        if (orderId is not null) q = q.Where(r => r.PurchaseOrderId == orderId);
        if (supplierId is not null) q = q.Where(r => r.SupplierId == supplierId);
        var list = await q.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.GrnNo).Take(500).ToListAsync(ct);
        var suppliers = await SuppliersAsync(db, list.Select(r => r.SupplierId), ct);
        var orderIds = list.Select(r => r.PurchaseOrderId).Distinct().ToList();
        var poNos = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.PoNo, ct);
        var items = await inv.GetItemsAsync(list.SelectMany(r => r.Lines.Select(l => l.ItemId)), ct);
        return list.Select(r => new ReceiptDto(r.Id, r.GrnNo, r.PurchaseOrderId, poNos[r.PurchaseOrderId], r.SupplierId, suppliers[r.SupplierId].NameEn,
            suppliers[r.SupplierId].NameAr, r.WarehouseId, r.ReceiptDate, r.SupplierDeliveryNote, r.Status.ToString(), r.TotalValue, r.JournalEntryId,
            r.Lines.OrderBy(l => l.LineNo).Select(l => new ReceiptLineDto(l.Id, l.LineNo, l.PurchaseOrderLineId, l.ItemId, items[l.ItemId].Sku,
                items[l.ItemId].NameEn, items[l.ItemId].NameAr, l.Quantity, l.UnitCost, l.Value, l.QuantityInvoiced)).ToList(), r.ExchangeRate)).ToList();
    }

    private static async Task<List<InvoiceDto>> InvoicesAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, Guid? supplierId, string? status, bool openOnly,
        CancellationToken ct, bool? opening = null)
    {
        var q = db.Set<SupplierInvoice>().AsNoTracking().Include(i => i.Lines).Where(i => i.CompanyId == t.RequireCompanyId());
        if (opening is { } o) q = q.Where(i => i.IsOpening == o);
        if (id is not null) q = q.Where(i => i.Id == id);
        if (supplierId is not null) q = q.Where(i => i.SupplierId == supplierId);
        if (Enum.TryParse<InvoiceStatus>(status, true, out var st)) q = q.Where(i => i.Status == st);
        if (openOnly) q = q.Where(i => i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid);
        var list = await q.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceNo).Take(500).ToListAsync(ct);
        var suppliers = await SuppliersAsync(db, list.Select(i => i.SupplierId), ct);
        var items = await inv.GetItemsAsync(list.SelectMany(i => i.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value)), ct);
        return list.Select(i => new InvoiceDto(i.Id, i.InvoiceNo, i.SupplierId, suppliers[i.SupplierId].NameEn, suppliers[i.SupplierId].NameAr, i.SupplierRef,
            i.InvoiceDate, i.DueDate, i.PurchaseOrderId, i.Total, i.WhtAmount, i.PaidAmount, i.OpenAmount, i.Status.ToString(), i.MatchStatus.ToString(),
            i.Notes, i.JournalEntryId,
            i.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineDto(l.Id, l.LineNo, l.LineType.ToString(), l.GoodsReceiptLineId, l.PurchaseOrderLineId,
                l.ItemId, l.ItemId is { } item ? items[item].Sku : null, l.Description, l.Quantity, l.UnitPrice, l.Amount, l.ReceiptUnitCost, l.Variance,
                l.WhtCategory.ToString(), l.WhtRate, l.WhtAmount, l.QuantityReturned)).ToList(), i.IsOpening, i.DebitedAmount, i.Currency, i.ExchangeRate,
                i.TotalBase, i.OpenBase)).ToList();
    }

    private static async Task<List<DebitNoteDto>> DebitNotesAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, Guid? supplierId,
        Guid? invoiceId, CancellationToken ct)
    {
        var q = db.Set<DebitNote>().AsNoTracking().Include(n => n.Lines).Where(n => n.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(n => n.Id == id);
        if (supplierId is not null) q = q.Where(n => n.SupplierId == supplierId);
        if (invoiceId is not null) q = q.Where(n => n.SupplierInvoiceId == invoiceId);
        var list = await q.OrderByDescending(n => n.NoteDate).ThenByDescending(n => n.DebitNoteNo).Take(500).ToListAsync(ct);
        var suppliers = await SuppliersAsync(db, list.Select(n => n.SupplierId), ct);
        var invoiceIds = list.Select(n => n.SupplierInvoiceId).Distinct().ToList();
        var invoices = await db.Set<SupplierInvoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new { i.InvoiceNo, i.SupplierRef }, ct);
        var items = await inv.GetItemsAsync(list.SelectMany(n => n.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value)), ct);
        return list.Select(n => new DebitNoteDto(n.Id, n.DebitNoteNo, n.SupplierId, suppliers[n.SupplierId].NameEn, suppliers[n.SupplierId].NameAr,
            n.SupplierInvoiceId, invoices[n.SupplierInvoiceId].InvoiceNo, invoices[n.SupplierInvoiceId].SupplierRef, n.NoteDate, n.WarehouseId, n.Reason, n.Total,
            n.Status.ToString(), n.JournalEntryId,
            n.Lines.OrderBy(l => l.LineNo).Select(l => new DebitNoteLineDto(l.LineNo, l.LineType.ToString(), l.SupplierInvoiceLineId, l.ItemId,
                l.ItemId is { } item ? items[item].Sku : null, l.Description, l.Quantity, l.UnitPrice, l.Amount, l.StockValue)).ToList())).ToList();
    }

    private static async Task<List<LandedCostDto>> LandedCostsAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, CancellationToken ct)
    {
        var q = db.Set<LandedCostVoucher>().AsNoTracking().Include(v => v.Charges).Include(v => v.Allocations).Where(v => v.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(v => v.Id == id);
        var list = await q.OrderByDescending(v => v.VoucherDate).ThenByDescending(v => v.VoucherNo).Take(500).ToListAsync(ct);
        var items = await inv.GetItemsAsync(list.SelectMany(v => v.Allocations.Select(a => a.ItemId)), ct);
        return list.Select(v => new LandedCostDto(v.Id, v.VoucherNo, v.VoucherDate, v.Method.ToString(), v.Status.ToString(), v.TotalAmount, v.Notes,
            v.JournalEntryId,
            v.Charges.OrderBy(c => c.LineNo).Select(c => new ChargeDto(c.LineNo, c.ChargeType.ToString(), c.Description, c.Amount, c.SupplierInvoiceId)).ToList(),
            v.Allocations.Select(a => new AllocationDto(a.GoodsReceiptLineId, a.ItemId, items[a.ItemId].Sku, a.Basis, a.Amount, a.ToInventory, a.ToCostOfSales))
                .OrderBy(a => a.Sku).ToList())).ToList();
    }

    private static async Task<List<PaymentDto>> PaymentsAsync(ErpDbContext db, ITenantContext t, Guid? id, Guid? supplierId, CancellationToken ct)
    {
        var q = db.Set<SupplierPayment>().AsNoTracking().Include(p => p.Allocations).Where(p => p.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(p => p.Id == id);
        if (supplierId is not null) q = q.Where(p => p.SupplierId == supplierId);
        var list = await q.OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.PaymentNo).Take(500).ToListAsync(ct);
        var suppliers = await SuppliersAsync(db, list.Select(p => p.SupplierId), ct);
        var invoiceIds = list.SelectMany(p => p.Allocations.Select(a => a.SupplierInvoiceId)).Distinct().ToList();
        var invoices = await db.Set<SupplierInvoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new { i.InvoiceNo, i.SupplierRef }, ct);
        return list.Select(p => new PaymentDto(p.Id, p.PaymentNo, p.SupplierId, suppliers[p.SupplierId].NameEn, suppliers[p.SupplierId].NameAr, p.PaymentDate,
            p.PaymentAccountId, p.Reference, p.GrossAmount, p.WhtAmount, p.NetAmount, p.Status.ToString(), p.JournalEntryId,
            p.Allocations.Select(a => new PaymentAllocationDto(a.SupplierInvoiceId, invoices[a.SupplierInvoiceId].InvoiceNo,
                invoices[a.SupplierInvoiceId].SupplierRef, a.Amount, a.WhtAmount)).ToList(), p.Currency, p.ExchangeRate, p.FxDifference)).ToList();
    }

    /// <summary>Received but not yet invoiced, at receipt cost, next to the GRNI account balance.</summary>
    private static async Task<GrniReport> GrniAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, IFinanceQueries finance, CancellationToken ct)
    {
        var rows = await (from l in db.Set<GoodsReceiptLine>().AsNoTracking()
                          join r in db.Set<GoodsReceipt>() on l.GoodsReceiptId equals r.Id
                          join s in db.Set<Supplier>() on r.SupplierId equals s.Id
                          where r.CompanyId == t.RequireCompanyId() && r.Status == DocumentStatus.Posted && l.QuantityInvoiced < l.Quantity
                          orderby r.ReceiptDate, r.GrnNo, l.LineNo
                          select new { r.Id, r.GrnNo, r.ReceiptDate, s.NameEn, s.NameAr, l.ItemId, l.Quantity, l.QuantityInvoiced, l.UnitCost, l.Value })
            .ToListAsync(ct);
        var items = await inv.GetItemsAsync(rows.Select(r => r.ItemId), ct);
        var result = rows.Select(r =>
        {
            var open = r.Quantity - r.QuantityInvoiced;
            var value = r.QuantityInvoiced == 0 ? r.Value : Money.Round(open * r.UnitCost);
            return new GrniRow(r.Id, r.GrnNo, r.ReceiptDate, r.NameEn, r.NameAr, items[r.ItemId].Sku, open, r.UnitCost, value);
        }).ToList();
        var total = result.Sum(r => r.Value);
        var ledger = -await finance.GetRoleBalanceAsync(AccountRoles.GoodsReceivedNotInvoiced, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(10), ct);
        return new GrniReport(result, total, ledger, total - ledger);
    }

    private static async Task<AgingReport> AgingAsync(ErpDbContext db, ITenantContext t, IFinanceQueries finance, CancellationToken ct, DateOnly? asOf = null)
    {
        var date = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var open = await (from i in db.Set<SupplierInvoice>().AsNoTracking()
                          join s in db.Set<Supplier>() on i.SupplierId equals s.Id
                          where i.CompanyId == t.RequireCompanyId() && (i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid)
                                && i.InvoiceDate <= date
                          select new { s.Id, s.Code, s.NameEn, s.NameAr, i.DueDate, Open = i.TotalBase - i.PaidBase - i.DebitedBase }).ToListAsync(ct);
        var rows = open.GroupBy(x => new { x.Id, x.Code, x.NameEn, x.NameAr }).Select(g =>
        {
            decimal Bucket(int from, int to) => g.Where(x => (date.DayNumber - x.DueDate.DayNumber) is var d && d >= from && d <= to).Sum(x => x.Open);
            return new AgingRow(g.Key.Id, g.Key.Code, g.Key.NameEn, g.Key.NameAr, Bucket(int.MinValue, 0), Bucket(1, 30), Bucket(31, 60), Bucket(61, 90),
                Bucket(91, int.MaxValue), g.Sum(x => x.Open));
        }).OrderByDescending(r => r.Total).ToList();
        var ledger = -await finance.GetRoleBalanceAsync(AccountRoles.AccountsPayable, date, ct);
        return new AgingReport(date, rows, rows.Sum(r => r.Total), ledger);
    }

    /// <summary>Tax withheld on payments in a month, for the return due by the 16th of the next month.</summary>
    private static async Task<WhtReport> WhtAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, int? year = null, int? month = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = new DateOnly(year ?? today.Year, month ?? today.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var rows = await (from a in db.Set<SupplierPaymentAllocation>().AsNoTracking()
                          join p in db.Set<SupplierPayment>() on a.SupplierPaymentId equals p.Id
                          join i in db.Set<SupplierInvoice>() on a.SupplierInvoiceId equals i.Id
                          join s in db.Set<Supplier>() on p.SupplierId equals s.Id
                          where p.CompanyId == t.RequireCompanyId() && p.Status == DocumentStatus.Posted && a.WhtAmount > 0
                                && p.PaymentDate >= start && p.PaymentDate <= end
                          orderby p.PaymentDate, p.PaymentNo
                          select new { p.PaymentDate, p.PaymentNo, s.NameEn, s.NameAr, s.Country, s.TaxCardNo, i.Id, i.InvoiceNo, a.Amount, a.WhtAmount, p.ExchangeRate })
            .ToListAsync(ct);
        var invoiceIds = rows.Select(r => r.Id).Distinct().ToList();
        var categories = (await db.Set<SupplierInvoiceLine>().AsNoTracking().Where(l => invoiceIds.Contains(l.SupplierInvoiceId) && l.WhtAmount > 0)
                .Select(l => new { l.SupplierInvoiceId, l.WhtCategory }).ToListAsync(ct))
            .GroupBy(l => l.SupplierInvoiceId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.WhtCategory.ToString()).Distinct()));
        var result = rows.Select(r => new WhtRow(r.PaymentDate, r.PaymentNo, r.NameEn, r.NameAr, r.Country, r.TaxCardNo, r.InvoiceNo,
            categories.GetValueOrDefault(r.Id, ""), PurchasingRules.ToBase(r.Amount, r.ExchangeRate), r.Amount == 0 ? 0 : Math.Round(r.WhtAmount * 100m / r.Amount, 2),
            PurchasingRules.ToBase(r.WhtAmount, r.ExchangeRate))).ToList();
        return new WhtReport(start.Year, start.Month, start.AddMonths(1).AddDays(15), result, result.Sum(r => r.Gross), result.Sum(r => r.Tax));
    }

    private static async Task<Dictionary<Guid, Supplier>> SuppliersAsync(ErpDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Supplier>().AsNoTracking().Where(s => list.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
    }
}

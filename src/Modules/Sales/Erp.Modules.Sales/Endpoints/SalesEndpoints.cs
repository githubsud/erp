using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Documents;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Endpoints;

public sealed record CustomerDto(Guid Id, string Code, string NameEn, string NameAr, string Country, string? TaxCardNo, string? CrNo, string? Email,
    string? Phone, string? AddressEn, string? AddressAr, int PaymentTermsDays, decimal? CreditLimit, Guid? PriceListId, Guid? SalespersonUserId, bool IsActive,
    string Currency = "QAR");

public sealed record SalesSettingsDto(string CreditControl, int QuotationValidityDays);

public sealed record PriceListItemDto(Guid ItemId, string Sku, string NameEn, string NameAr, decimal UnitPrice);

public sealed record PriceListDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive, IReadOnlyList<PriceListItemDto> Items);

public sealed record PriceDto(Guid ItemId, decimal? UnitPrice);

public sealed record SalesLineDto(Guid Id, int LineNo, Guid ItemId, string Sku, string NameEn, string NameAr, string ItemType, string UomCode, string? Description,
    decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal Amount, decimal QuantityDelivered, decimal QuantityInvoiced, Guid? UomId,
    string? OrderUomCode, decimal UomFactor, decimal UomQuantity, decimal UomPrice);

public sealed record QuotationDto(Guid Id, string QuoteNo, Guid CustomerId, string CustomerCode, string CustomerNameEn, string CustomerNameAr, DateOnly QuoteDate,
    DateOnly ValidUntil, bool IsExpired, string Status, decimal Total, string? Notes, Guid? SalesOrderId, IReadOnlyList<SalesLineDto> Lines,
    string Currency = "QAR", decimal ExchangeRate = 1m);

public sealed record SalesOrderDto(Guid Id, string SoNo, Guid CustomerId, string CustomerCode, string CustomerNameEn, string CustomerNameAr, DateOnly OrderDate,
    DateOnly? ExpectedDate, Guid WarehouseId, string? CustomerRef, Guid? QuotationId, string Status, decimal Total, string? Notes, string? CreditWarning,
    DateTimeOffset? CreditApprovedAt, IReadOnlyList<SalesLineDto> Lines, string Currency = "QAR", decimal ExchangeRate = 1m);

public sealed record DeliveryLineDto(Guid Id, int LineNo, Guid SalesOrderLineId, Guid ItemId, string Sku, string NameEn, string NameAr, string UomCode,
    decimal Quantity, decimal UnitCost, decimal Cost, decimal QuantityInvoiced);

public sealed record DeliveryNoteDto(Guid Id, string DnNo, Guid SalesOrderId, string SoNo, Guid CustomerId, string CustomerNameEn, string CustomerNameAr,
    Guid WarehouseId, DateOnly DeliveryDate, string? DeliveryAddress, string? VehicleNo, string? Notes, string Status, decimal TotalCost, Guid? JournalEntryId,
    IReadOnlyList<DeliveryLineDto> Lines);

public sealed record SalesInvoiceLineDto(Guid Id, int LineNo, string LineType, Guid? DeliveryNoteLineId, Guid? SalesOrderLineId, Guid? ItemId, string? Sku,
    string? NameEn, string? NameAr, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal Amount, decimal? UnitCost,
    decimal Cost, decimal QuantityReturned, Guid? UomId = null, decimal UomQuantity = 0, decimal UomPrice = 0);

public sealed record SalesInvoiceDto(Guid Id, string InvoiceNo, Guid CustomerId, string CustomerCode, string CustomerNameEn, string CustomerNameAr,
    DateOnly InvoiceDate, DateOnly DueDate, Guid? SalesOrderId, Guid? WarehouseId, string? CustomerRef, decimal Total, decimal DiscountTotal, decimal PaidAmount,
    decimal CreditedAmount, decimal OpenAmount, string Status, string? Notes, Guid? EInvoiceUuid, DateTimeOffset? IssuedAt, Guid? JournalEntryId,
    IReadOnlyList<SalesInvoiceLineDto> Lines, bool IsOpening, string Currency = "QAR", decimal ExchangeRate = 1m, decimal TotalBase = 0, decimal OpenBase = 0,
    Guid? ProjectId = null, decimal RetentionAmount = 0);

public sealed record CreditNoteLineDto(int LineNo, string LineType, Guid? SalesInvoiceLineId, Guid? ItemId, string? Sku, string? Description, decimal Quantity,
    decimal UnitPrice, decimal Amount, decimal Cost);

public sealed record CreditNoteDto(Guid Id, string CreditNoteNo, Guid CustomerId, string CustomerNameEn, string CustomerNameAr, Guid SalesInvoiceId,
    string InvoiceNo, DateOnly NoteDate, Guid? WarehouseId, string Reason, decimal Total, decimal CostReturned, string Status, Guid? JournalEntryId,
    IReadOnlyList<CreditNoteLineDto> Lines, decimal UnappliedAmount = 0);

public sealed record ReceiptAllocationDto(Guid SalesInvoiceId, string InvoiceNo, decimal Amount);

public sealed record ReceiptDto(Guid Id, string ReceiptNo, Guid CustomerId, string CustomerNameEn, string CustomerNameAr, DateOnly ReceiptDate, string Method,
    Guid? DepositAccountId, string? Reference, string? ChequeNo, DateOnly? ChequeDate, string? ChequeBank, decimal Amount, string Status, DateOnly? ClearedDate,
    string? StatusReason, Guid? JournalEntryId, IReadOnlyList<ReceiptAllocationDto> Allocations, decimal UnappliedAmount = 0, string Currency = "QAR",
    decimal ExchangeRate = 1m, decimal AmountBase = 0, decimal FxDifference = 0);

public sealed record CustomerAgingRow(Guid CustomerId, string Code, string NameEn, string NameAr, decimal Current, decimal Days1To30, decimal Days31To60,
    decimal Days61To90, decimal Over90, decimal Total, decimal? CreditLimit, decimal UnappliedCredit = 0);

public sealed record CustomerAgingReport(DateOnly AsOf, IReadOnlyList<CustomerAgingRow> Rows, decimal Total, decimal LedgerBalance);

public sealed record StatementLine(DateOnly Date, string SourceType, Guid SourceId, string SourceNo, string? MemoEn, string? MemoAr, decimal Debit,
    decimal Credit, decimal Balance);

public sealed record StatementDto(Guid CustomerId, string Code, string NameEn, string NameAr, DateOnly From, DateOnly To, decimal OpeningBalance,
    IReadOnlyList<StatementLine> Lines, decimal ClosingBalance);

public sealed record PdcRow(Guid ReceiptId, string ReceiptNo, Guid CustomerId, string CustomerNameEn, string CustomerNameAr, string? ChequeNo,
    string? ChequeBank, DateOnly ChequeDate, decimal Amount, int DaysToDue);

public sealed record PdcReport(IReadOnlyList<PdcRow> Rows, decimal Total, decimal LedgerBalance);

public sealed record UninvoicedRow(Guid DeliveryNoteId, string DnNo, DateOnly DeliveryDate, Guid CustomerId, string CustomerNameEn, string CustomerNameAr,
    string Sku, string NameEn, string NameAr, decimal Quantity, decimal Value);

public sealed record SalesAnalysisRow(Guid? Id, string Code, string NameEn, string NameAr, decimal Quantity, decimal Revenue, decimal Returns,
    decimal NetRevenue, decimal Cost, decimal Margin, decimal? MarginPercent);

public sealed record SalesAnalysisReport(DateOnly From, DateOnly To, string GroupBy, IReadOnlyList<SalesAnalysisRow> Rows, decimal NetRevenue, decimal Cost,
    decimal Margin);

public sealed record OpeningInvoiceResult(Guid Id, string InvoiceNo, Guid PartyId, string Reference, DateOnly InvoiceDate, DateOnly DueDate,
    decimal Amount);

internal static class SalesEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales").WithTags("Sales").RequireAuthorization();

        g.MapGet("/settings", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var s = await db.Set<SalesSettings>().AsNoTracking().SingleAsync(x => x.CompanyId == t.RequireCompanyId(), ct);
            return new SalesSettingsDto(s.CreditControl.ToString(), s.QuotationValidityDays);
        }).RequirePermission(SalesPermissions.CustomersRead);
        g.MapPut("/settings", async (SalesSettingsRequest r, CustomerService s, CancellationToken ct) =>
        {
            var x = await s.SaveSettingsAsync(r, ct);
            return new SalesSettingsDto(x.CreditControl.ToString(), x.QuotationValidityDays);
        }).RequirePermission(SalesPermissions.SettingsManage);

        // Customers and prices
        g.MapGet("/customers", async (ErpDbContext db, ITenantContext t, CancellationToken ct, string? search = null) =>
        {
            var q = db.Set<Customer>().AsNoTracking().Where(c => c.CompanyId == t.RequireCompanyId());
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                q = q.Where(c => EF.Functions.ILike(c.Code, $"%{term}%") || EF.Functions.ILike(c.NameEn, $"%{term}%") || c.NameAr.Contains(term) ||
                                 (c.Phone != null && c.Phone.Contains(term)));
            }
            return (await q.OrderBy(c => c.Code).Take(1000).ToListAsync(ct)).Select(ToDto);
        }).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/customers/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            ToDto(await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == t.RequireCompanyId(), ct)
                  ?? throw new NotFoundException("Customer", id))).RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/customers", async (CustomerRequest r, CustomerService s, CancellationToken ct) => ToDto(await s.SaveAsync(null, r, ct)))
            .RequirePermission(SalesPermissions.CustomersManage);
        g.MapPut("/customers/{id:guid}", async (Guid id, CustomerRequest r, CustomerService s, CancellationToken ct) => ToDto(await s.SaveAsync(id, r, ct)))
            .RequirePermission(SalesPermissions.CustomersManage);
        g.MapGet("/customers/{id:guid}/exposure", (Guid id, CreditControlService s, CancellationToken ct) => s.ExposureAsync(id, null, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/customers/{id:guid}/statement", StatementAsync).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/customers/{id:guid}/statement.pdf", async (Guid id, DateOnly from, DateOnly to, SalesPdfDocuments pdf, CancellationToken ct) =>
        {
            var (content, name) = await pdf.StatementAsync(id, from, to, ct);
            return Results.File(content, "application/pdf", name);
        }).RequirePermission(SalesPermissions.CustomersRead);

        g.MapGet("/price-lists", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) => await PriceListsAsync(db, t, inv, null, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/price-lists", async (PriceListRequest r, CustomerService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await PriceListsAsync(db, t, inv, (await s.SavePriceListAsync(null, r, ct)).Id, ct)).Single()).RequirePermission(SalesPermissions.CustomersManage);
        g.MapPut("/price-lists/{id:guid}", async (Guid id, PriceListRequest r, CustomerService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) => (await PriceListsAsync(db, t, inv, (await s.SavePriceListAsync(id, r, ct)).Id, ct)).Single())
            .RequirePermission(SalesPermissions.CustomersManage);
        g.MapGet("/prices", async (string itemIds, CustomerService s, CancellationToken ct, Guid? customerId = null, decimal? exchangeRate = null) =>
        {
            var ids = itemIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToList();
            return (await s.PricesAsync(customerId, ids, ct, exchangeRate is > 0 ? exchangeRate.Value : 1m)).Select(p => new PriceDto(p.Key, p.Value));
        }).RequirePermission(SalesPermissions.CustomersRead);

        // Quotations
        g.MapGet("/quotations", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock, CancellationToken ct, string? status = null,
            Guid? customerId = null) => await QuotationsAsync(db, t, inv, clock, null, status, customerId, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/quotations/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock, CancellationToken ct) =>
            await QuotationAsync(db, t, inv, clock, id, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/quotations", async (QuotationRequest r, QuotationService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock,
            CancellationToken ct) => await QuotationAsync(db, t, inv, clock, (await s.CreateAsync(r, ct)).Id, ct)).RequirePermission(SalesPermissions.OrdersManage);
        g.MapPut("/quotations/{id:guid}", async (Guid id, QuotationRequest r, QuotationService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            IClock clock, CancellationToken ct) => await QuotationAsync(db, t, inv, clock, (await s.UpdateAsync(id, r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.OrdersManage);
        g.MapDelete("/quotations/{id:guid}", async (Guid id, QuotationService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(SalesPermissions.OrdersManage);
        g.MapPost("/quotations/{id:guid}/send", async (Guid id, QuotationService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock,
            CancellationToken ct) => await QuotationAsync(db, t, inv, clock, (await s.MarkSentAsync(id, ct)).Id, ct)).RequirePermission(SalesPermissions.OrdersManage);
        g.MapPost("/quotations/{id:guid}/reject", async (Guid id, QuotationService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock,
            CancellationToken ct) => await QuotationAsync(db, t, inv, clock, (await s.RejectAsync(id, ct)).Id, ct)).RequirePermission(SalesPermissions.OrdersManage);
        g.MapPost("/quotations/{id:guid}/convert", async (Guid id, ConvertQuotationRequest r, QuotationService s, ErpDbContext db, ITenantContext t,
            IInventoryQueries inv, CancellationToken ct) => await OrderAsync(db, t, inv, (await s.ConvertAsync(id, r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.OrdersManage);
        g.MapGet("/quotations/{id:guid}/pdf", async (Guid id, SalesPdfDocuments pdf, CancellationToken ct) => File(await pdf.QuotationAsync(id, ct)))
            .RequirePermission(SalesPermissions.CustomersRead);

        // Sales orders
        g.MapGet("/orders", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, string? status = null, Guid? customerId = null) =>
            await OrdersAsync(db, t, inv, null, status, customerId, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/orders/{id:guid}", (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) => OrderAsync(db, t, inv, id, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/orders", async (SalesOrderRequest r, SalesOrderService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            await OrderAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, ct)).RequirePermission(SalesPermissions.OrdersManage);
        g.MapPut("/orders/{id:guid}", async (Guid id, SalesOrderRequest r, SalesOrderService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) => await OrderAsync(db, t, inv, (await s.UpdateAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.OrdersManage);
        MapOrderAction(g, "confirm", (s, id, ct) => s.ConfirmAsync(id, ct), SalesPermissions.OrdersManage);
        MapOrderAction(g, "approve-credit", (s, id, ct) => s.ApproveCreditAsync(id, ct), SalesPermissions.CreditApprove);
        MapOrderAction(g, "reject", (s, id, ct) => s.RejectAsync(id, ct), SalesPermissions.CreditApprove);
        MapOrderAction(g, "cancel", (s, id, ct) => s.CancelAsync(id, ct), SalesPermissions.OrdersManage);
        MapOrderAction(g, "close", (s, id, ct) => s.CloseAsync(id, ct), SalesPermissions.OrdersManage);

        // Delivery notes
        g.MapGet("/deliveries", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? salesOrderId = null, Guid? customerId = null) =>
            await DeliveriesAsync(db, t, inv, null, salesOrderId, customerId, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/deliveries/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await DeliveriesAsync(db, t, inv, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Delivery note", id))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/deliveries", async (DeliveryRequest r, DeliveryNoteService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await DeliveriesAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.DeliveriesPost);
        g.MapPost("/deliveries/{id:guid}/reverse", async (Guid id, ReverseRequest r, DeliveryNoteService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await DeliveriesAsync(db, t, inv, id, null, null, ct)).Single();
        }).RequirePermission(SalesPermissions.DeliveriesPost);
        g.MapPost("/deliveries/{id:guid}/invoice", async (Guid id, InvoiceDeliveryRequest r, SalesInvoiceService s, ErpDbContext db, ITenantContext t,
            IInventoryQueries inv, CancellationToken ct) => await InvoiceAsync(db, t, inv, (await s.FromDeliveryAsync(id, r, ct)).Id, ct))
            .RequirePermission(SalesPermissions.InvoicesPost);
        g.MapGet("/deliveries/{id:guid}/pdf", async (Guid id, SalesPdfDocuments pdf, CancellationToken ct) => File(await pdf.DeliveryNoteAsync(id, ct)))
            .RequirePermission(SalesPermissions.CustomersRead);

        // Invoices
        g.MapGet("/invoices", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? customerId = null, string? status = null,
            bool openOnly = false, bool? opening = null) => await InvoicesAsync(db, t, inv, null, customerId, status, openOnly, ct, opening))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/opening-balances", async (CustomerOpeningRequest r, CustomerOpeningService s, CancellationToken ct) =>
            (await s.CreateAsync(r, ct)).Select(i => new OpeningInvoiceResult(i.Id, i.InvoiceNo, i.CustomerId, i.CustomerRef ?? "", i.InvoiceDate, i.DueDate,
                i.Total)).ToList()).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapGet("/invoices/{id:guid}", (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) => InvoiceAsync(db, t, inv, id, ct))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/invoices", async (InvoiceRequest r, SalesInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            await InvoiceAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, ct)).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapDelete("/invoices/{id:guid}", async (Guid id, SalesInvoiceService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapPost("/invoices/{id:guid}/post", async (Guid id, SalesInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            await InvoiceAsync(db, t, inv, (await s.PostAsync(id, ct)).Id, ct)).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapPost("/invoices/{id:guid}/reverse", async (Guid id, ReverseRequest r, SalesInvoiceService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) => await InvoiceAsync(db, t, inv, (await s.ReverseAsync(id, r, ct)).Id, ct)).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapGet("/invoices/{id:guid}/pdf", async (Guid id, SalesPdfDocuments pdf, CancellationToken ct) => File(await pdf.InvoiceAsync(id, ct)))
            .RequirePermission(SalesPermissions.CustomersRead);

        // Credit notes
        g.MapGet("/credit-notes", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, Guid? customerId = null, Guid? salesInvoiceId = null) =>
            await CreditNotesAsync(db, t, inv, null, customerId, salesInvoiceId, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/credit-notes/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await CreditNotesAsync(db, t, inv, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Credit note", id))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/credit-notes", async (CreditNoteRequest r, CreditNoteService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct) =>
            (await CreditNotesAsync(db, t, inv, (await s.CreateAsync(r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapPost("/credit-notes/{id:guid}/reverse", async (Guid id, ReverseRequest r, CreditNoteService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await CreditNotesAsync(db, t, inv, id, null, null, ct)).Single();
        }).RequirePermission(SalesPermissions.InvoicesPost);
        g.MapGet("/credit-notes/{id:guid}/pdf", async (Guid id, SalesPdfDocuments pdf, CancellationToken ct) => File(await pdf.CreditNoteAsync(id, ct)))
            .RequirePermission(SalesPermissions.CustomersRead);

        // Receipts and cheques
        g.MapGet("/receipts", async (ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? customerId = null, string? status = null) =>
            await ReceiptsAsync(db, t, null, customerId, status, ct)).RequirePermission(SalesPermissions.CustomersRead);
        g.MapGet("/receipts/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Customer receipt", id))
            .RequirePermission(SalesPermissions.CustomersRead);
        g.MapPost("/receipts", async (ReceiptRequest r, CustomerReceiptService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, (await s.CreateAsync(r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.ReceiptsPost);
        g.MapPost("/receipts/{id:guid}/deposit", async (Guid id, PdcDepositRequest r, CustomerReceiptService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, (await s.DepositAsync(id, r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.ReceiptsPost);
        g.MapPost("/receipts/{id:guid}/bounce", async (Guid id, ReverseRequest r, CustomerReceiptService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, (await s.BounceAsync(id, r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.ReceiptsPost);
        g.MapPost("/receipts/{id:guid}/reverse", async (Guid id, ReverseRequest r, CustomerReceiptService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await ReceiptsAsync(db, t, (await s.ReverseAsync(id, r, ct)).Id, null, null, ct)).Single()).RequirePermission(SalesPermissions.ReceiptsPost);

        // Reports
        g.MapGet("/reports/aging", AgingAsync).RequirePermission(SalesPermissions.ReportsRead);
        g.MapGet("/reports/pdc", PdcAsync).RequirePermission(SalesPermissions.ReportsRead);
        g.MapGet("/reports/uninvoiced", UninvoicedAsync).RequirePermission(SalesPermissions.ReportsRead);
        g.MapGet("/reports/analysis", AnalysisAsync).RequirePermission(SalesPermissions.ReportsRead);
    }

    private static IResult File((byte[] Content, string FileName) file) => Results.File(file.Content, "application/pdf", file.FileName);

    private static void MapOrderAction(RouteGroupBuilder g, string action, Func<SalesOrderService, Guid, CancellationToken, Task<SalesOrder>> run, string permission) =>
        g.MapPost($"/orders/{{id:guid}}/{action}", async (Guid id, SalesOrderService s, ErpDbContext db, ITenantContext t, IInventoryQueries inv,
            CancellationToken ct) => await OrderAsync(db, t, inv, (await run(s, id, ct)).Id, ct)).RequirePermission(permission);

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.Country, c.TaxCardNo, c.CrNo, c.Email, c.Phone, c.AddressEn,
        c.AddressAr, c.PaymentTermsDays, c.CreditLimit, c.PriceListId, c.SalespersonUserId, c.IsActive, c.Currency);

    private static async Task<Dictionary<Guid, Customer>> CustomersAsync(ErpDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Customer>().AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
    }

    private static SalesLineDto Line(IPricedLine l, Guid id, int lineNo, string? description, decimal delivered, decimal invoiced,
        IReadOnlyDictionary<Guid, ItemInfo> items, IReadOnlyDictionary<Guid, string> uomCodes) =>
        new(id, lineNo, l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn, items[l.ItemId].NameAr, items[l.ItemId].Type.ToString(),
            items[l.ItemId].UomCode, description, l.Quantity, l.UnitPrice, l.DiscountPercent, l.Amount, delivered, invoiced, l.UomId,
            l.UomId is { } u ? uomCodes.GetValueOrDefault(u) : null, l.UomFactor, l.UomQuantity, l.UomPrice);

    private static async Task<Dictionary<Guid, string>> UomCodesAsync(IInventoryQueries inv, IEnumerable<IPricedLine> lines, CancellationToken ct)
    {
        var withUnit = lines.Where(l => l.UomId != null).ToList();
        if (withUnit.Count == 0) return [];
        var ids = withUnit.Select(l => l.UomId!.Value).ToHashSet();
        return (await inv.GetUnitsAsync(withUnit.Select(l => l.ItemId), ct)).Where(u => ids.Contains(u.UomId))
            .GroupBy(u => u.UomId).ToDictionary(g => g.Key, g => g.First().UomCode);
    }

    private static async Task<List<PriceListDto>> PriceListsAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, CancellationToken ct)
    {
        var q = db.Set<PriceList>().AsNoTracking().Include(p => p.Items).Where(p => p.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(p => p.Id == id);
        var lists = await q.OrderBy(p => p.Code).ToListAsync(ct);
        var items = await inv.GetItemsAsync(lists.SelectMany(p => p.Items.Select(i => i.ItemId)), ct);
        return lists.Select(p => new PriceListDto(p.Id, p.Code, p.NameEn, p.NameAr, p.IsActive,
            p.Items.Select(i => new PriceListItemDto(i.ItemId, items[i.ItemId].Sku, items[i.ItemId].NameEn, items[i.ItemId].NameAr, i.UnitPrice))
                .OrderBy(i => i.Sku).ToList())).ToList();
    }

    private static async Task<QuotationDto> QuotationAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock, Guid id, CancellationToken ct) =>
        (await QuotationsAsync(db, t, inv, clock, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Quotation", id);

    private static async Task<List<QuotationDto>> QuotationsAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock, Guid? id, string? status,
        Guid? customerId, CancellationToken ct)
    {
        var q = db.Set<Quotation>().AsNoTracking().Include(x => x.Lines).Where(x => x.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(x => x.Id == id);
        if (Enum.TryParse<QuotationStatus>(status, true, out var st)) q = q.Where(x => x.Status == st);
        if (customerId is not null) q = q.Where(x => x.CustomerId == customerId);
        var list = await q.OrderByDescending(x => x.QuoteDate).ThenByDescending(x => x.QuoteNo).Take(500).ToListAsync(ct);
        var customers = await CustomersAsync(db, list.Select(x => x.CustomerId), ct);
        var items = await inv.GetItemsAsync(list.SelectMany(x => x.Lines.Select(l => l.ItemId)), ct);
        var codes = await UomCodesAsync(inv, list.SelectMany(x => x.Lines), ct);
        return list.Select(x => new QuotationDto(x.Id, x.QuoteNo, x.CustomerId, customers[x.CustomerId].Code, customers[x.CustomerId].NameEn,
            customers[x.CustomerId].NameAr, x.QuoteDate, x.ValidUntil,
            x.Status is QuotationStatus.Draft or QuotationStatus.Sent && x.ValidUntil < clock.Today, x.Status.ToString(), x.Total, x.Notes, x.SalesOrderId,
            x.Lines.OrderBy(l => l.LineNo).Select(l => Line(l, l.Id, l.LineNo, l.Description, 0, 0, items, codes)).ToList(), x.Currency, x.ExchangeRate)).ToList();
    }

    private static async Task<SalesOrderDto> OrderAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid id, CancellationToken ct) =>
        (await OrdersAsync(db, t, inv, id, null, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Sales order", id);

    private static async Task<List<SalesOrderDto>> OrdersAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, string? status, Guid? customerId,
        CancellationToken ct)
    {
        var q = db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines).Where(o => o.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(o => o.Id == id);
        if (Enum.TryParse<SalesOrderStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
        if (customerId is not null) q = q.Where(o => o.CustomerId == customerId);
        var orders = await q.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.SoNo).Take(500).ToListAsync(ct);
        var customers = await CustomersAsync(db, orders.Select(o => o.CustomerId), ct);
        var items = await inv.GetItemsAsync(orders.SelectMany(o => o.Lines.Select(l => l.ItemId)), ct);
        var codes = await UomCodesAsync(inv, orders.SelectMany(o => o.Lines), ct);
        return orders.Select(o => new SalesOrderDto(o.Id, o.SoNo, o.CustomerId, customers[o.CustomerId].Code, customers[o.CustomerId].NameEn,
            customers[o.CustomerId].NameAr, o.OrderDate, o.ExpectedDate, o.WarehouseId, o.CustomerRef, o.QuotationId, o.Status.ToString(), o.Total, o.Notes,
            o.CreditWarning, o.CreditApprovedAt,
            o.Lines.OrderBy(l => l.LineNo).Select(l => Line(l, l.Id, l.LineNo, l.Description, l.QuantityDelivered, l.QuantityInvoiced, items, codes)).ToList(),
            o.Currency, o.ExchangeRate)).ToList();
    }

    private static async Task<List<DeliveryNoteDto>> DeliveriesAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, Guid? orderId,
        Guid? customerId, CancellationToken ct)
    {
        var q = db.Set<DeliveryNote>().AsNoTracking().Include(d => d.Lines).Where(d => d.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(d => d.Id == id);
        if (orderId is not null) q = q.Where(d => d.SalesOrderId == orderId);
        if (customerId is not null) q = q.Where(d => d.CustomerId == customerId);
        var list = await q.OrderByDescending(d => d.DeliveryDate).ThenByDescending(d => d.DnNo).Take(500).ToListAsync(ct);
        var customers = await CustomersAsync(db, list.Select(d => d.CustomerId), ct);
        var orderIds = list.Select(d => d.SalesOrderId).Distinct().ToList();
        var soNos = await db.Set<SalesOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.SoNo, ct);
        var items = await inv.GetItemsAsync(list.SelectMany(d => d.Lines.Select(l => l.ItemId)), ct);
        return list.Select(d => new DeliveryNoteDto(d.Id, d.DnNo, d.SalesOrderId, soNos[d.SalesOrderId], d.CustomerId, customers[d.CustomerId].NameEn,
            customers[d.CustomerId].NameAr, d.WarehouseId, d.DeliveryDate, d.DeliveryAddress, d.VehicleNo, d.Notes, d.Status.ToString(), d.TotalCost,
            d.JournalEntryId,
            d.Lines.OrderBy(l => l.LineNo).Select(l => new DeliveryLineDto(l.Id, l.LineNo, l.SalesOrderLineId, l.ItemId, items[l.ItemId].Sku,
                items[l.ItemId].NameEn, items[l.ItemId].NameAr, items[l.ItemId].UomCode, l.Quantity, l.UnitCost, l.Cost, l.QuantityInvoiced)).ToList())).ToList();
    }

    private static async Task<SalesInvoiceDto> InvoiceAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid id, CancellationToken ct) =>
        (await InvoicesAsync(db, t, inv, id, null, null, false, ct)).SingleOrDefault() ?? throw new NotFoundException("Sales invoice", id);

    private static async Task<List<SalesInvoiceDto>> InvoicesAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, Guid? customerId,
        string? status, bool openOnly, CancellationToken ct, bool? opening = null)
    {
        var q = db.Set<SalesInvoice>().AsNoTracking().Include(i => i.Lines).Where(i => i.CompanyId == t.RequireCompanyId());
        if (opening is { } o) q = q.Where(i => i.IsOpening == o);
        if (id is not null) q = q.Where(i => i.Id == id);
        if (customerId is not null) q = q.Where(i => i.CustomerId == customerId);
        if (Enum.TryParse<InvoiceStatus>(status, true, out var st)) q = q.Where(i => i.Status == st);
        if (openOnly) q = q.Where(i => i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid);
        var list = await q.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceNo).Take(500).ToListAsync(ct);
        var customers = await CustomersAsync(db, list.Select(i => i.CustomerId), ct);
        var items = await inv.GetItemsAsync(list.SelectMany(i => i.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value)), ct);
        return list.Select(i => new SalesInvoiceDto(i.Id, i.InvoiceNo, i.CustomerId, customers[i.CustomerId].Code, customers[i.CustomerId].NameEn,
            customers[i.CustomerId].NameAr, i.InvoiceDate, i.DueDate, i.SalesOrderId, i.WarehouseId, i.CustomerRef, i.Total, i.DiscountTotal, i.PaidAmount,
            i.CreditedAmount, i.OpenAmount, i.Status.ToString(), i.Notes, i.EInvoiceUuid, i.IssuedAt, i.JournalEntryId,
            i.Lines.OrderBy(l => l.LineNo).Select(l => new SalesInvoiceLineDto(l.Id, l.LineNo, l.LineType.ToString(), l.DeliveryNoteLineId, l.SalesOrderLineId,
                l.ItemId, l.ItemId is { } a ? items[a].Sku : null, l.ItemId is { } b ? items[b].NameEn : null, l.ItemId is { } c ? items[c].NameAr : null,
                l.Description, l.Quantity, l.UnitPrice, l.DiscountPercent, l.Amount, l.UnitCost, l.Cost, l.QuantityReturned, l.UomId, l.UomQuantity, l.UomPrice))
                .ToList(), i.IsOpening,
            i.Currency, i.ExchangeRate, i.TotalBase, i.OpenBase, i.ProjectId, i.RetentionAmount)).ToList();
    }

    private static async Task<List<CreditNoteDto>> CreditNotesAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, Guid? id, Guid? customerId,
        Guid? invoiceId, CancellationToken ct)
    {
        var q = db.Set<CreditNote>().AsNoTracking().Include(n => n.Lines).Where(n => n.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(n => n.Id == id);
        if (customerId is not null) q = q.Where(n => n.CustomerId == customerId);
        if (invoiceId is not null) q = q.Where(n => n.SalesInvoiceId == invoiceId);
        var list = await q.OrderByDescending(n => n.NoteDate).ThenByDescending(n => n.CreditNoteNo).Take(500).ToListAsync(ct);
        var customers = await CustomersAsync(db, list.Select(n => n.CustomerId), ct);
        var invoiceIds = list.Select(n => n.SalesInvoiceId).Distinct().ToList();
        var invoiceNos = await db.Set<SalesInvoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.InvoiceNo, ct);
        var items = await inv.GetItemsAsync(list.SelectMany(n => n.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value)), ct);
        return list.Select(n => new CreditNoteDto(n.Id, n.CreditNoteNo, n.CustomerId, customers[n.CustomerId].NameEn, customers[n.CustomerId].NameAr,
            n.SalesInvoiceId, invoiceNos[n.SalesInvoiceId], n.NoteDate, n.WarehouseId, n.Reason, n.Total, n.CostReturned, n.Status.ToString(), n.JournalEntryId,
            n.Lines.OrderBy(l => l.LineNo).Select(l => new CreditNoteLineDto(l.LineNo, l.LineType.ToString(), l.SalesInvoiceLineId, l.ItemId,
                l.ItemId is { } item ? items[item].Sku : null, l.Description, l.Quantity, l.UnitPrice, l.Amount, l.Cost)).ToList(), n.UnappliedAmount)).ToList();
    }

    private static async Task<List<ReceiptDto>> ReceiptsAsync(ErpDbContext db, ITenantContext t, Guid? id, Guid? customerId, string? status, CancellationToken ct)
    {
        var q = db.Set<CustomerReceipt>().AsNoTracking().Include(r => r.Allocations).Where(r => r.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(r => r.Id == id);
        if (customerId is not null) q = q.Where(r => r.CustomerId == customerId);
        if (Enum.TryParse<ReceiptStatus>(status, true, out var st)) q = q.Where(r => r.Status == st);
        var list = await q.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.ReceiptNo).Take(500).ToListAsync(ct);
        var customers = await CustomersAsync(db, list.Select(r => r.CustomerId), ct);
        var invoiceIds = list.SelectMany(r => r.Allocations.Select(a => a.SalesInvoiceId)).Distinct().ToList();
        var invoiceNos = await db.Set<SalesInvoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.InvoiceNo, ct);
        return list.Select(r => new ReceiptDto(r.Id, r.ReceiptNo, r.CustomerId, customers[r.CustomerId].NameEn, customers[r.CustomerId].NameAr, r.ReceiptDate,
            r.Method.ToString(), r.DepositAccountId, r.Reference, r.ChequeNo, r.ChequeDate, r.ChequeBank, r.Amount, r.Status.ToString(), r.ClearedDate,
            r.StatusReason, r.JournalEntryId,
            r.Allocations.Select(a => new ReceiptAllocationDto(a.SalesInvoiceId, invoiceNos[a.SalesInvoiceId], a.Amount)).ToList(), r.UnappliedAmount,
            r.Currency, r.ExchangeRate, r.AmountBase, r.FxDifference)).ToList();
    }

    private static async Task<StatementDto> StatementAsync(Guid id, ErpDbContext db, ITenantContext t, IFinanceQueries finance, IClock clock, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null)
    {
        var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == t.RequireCompanyId(), ct)
                       ?? throw new NotFoundException("Customer", id);
        var end = to ?? clock.Today;
        var start = from ?? new DateOnly(end.Year, 1, 1);
        var ledger = await finance.GetPartyLedgerAsync(AccountRoles.AccountsReceivable, PartyType.Customer, id, start, end, ct);
        var balance = ledger.OpeningBalance;
        var lines = ledger.Lines.Select(l =>
        {
            balance += l.Debit - l.Credit;
            return new StatementLine(l.PostingDate, l.SourceType, l.SourceId, l.SourceNo, l.MemoEn, l.MemoAr, l.Debit, l.Credit, balance);
        }).ToList();
        return new StatementDto(customer.Id, customer.Code, customer.NameEn, customer.NameAr, start, end, ledger.OpeningBalance, lines, ledger.ClosingBalance);
    }

    /// <summary>Open invoices by days past their due date, next to the receivable account balance.</summary>
    private static async Task<CustomerAgingReport> AgingAsync(ErpDbContext db, ITenantContext t, IFinanceQueries finance, IClock clock, CancellationToken ct,
        DateOnly? asOf = null)
    {
        var date = asOf ?? clock.Today;
        var open = await (from i in db.Set<SalesInvoice>().AsNoTracking()
                          join c in db.Set<Customer>() on i.CustomerId equals c.Id
                          where i.CompanyId == t.RequireCompanyId() && (i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid)
                                && i.InvoiceDate <= date
                          select new { c.Id, c.Code, c.NameEn, c.NameAr, c.CreditLimit, i.DueDate, Open = i.TotalBase - i.PaidBase - i.CreditedBase - i.RetentionAmount * i.ExchangeRate })
            .ToListAsync(ct);
        // Unapplied credits (receipts on account, credit notes beyond the invoice) reduce what the customer owes.
        var credits = (await new CustomerCreditServiceQueries(db, t).UnappliedAsync(date, ct));
        var customers = open.Select(x => new { x.Id, x.Code, x.NameEn, x.NameAr, x.CreditLimit })
            .Concat(credits.Select(c => new { Id = c.CustomerId, c.Code, c.NameEn, c.NameAr, c.CreditLimit })).DistinctBy(x => x.Id);
        var rows = customers.Select(k =>
        {
            var mine = open.Where(x => x.Id == k.Id).ToList();
            decimal Bucket(int b) => mine.Where(x => SalesRules.AgingBucket(x.DueDate, date) == b).Sum(x => x.Open);
            var credit = credits.Where(c => c.CustomerId == k.Id).Sum(c => c.Amount);
            return new CustomerAgingRow(k.Id, k.Code, k.NameEn, k.NameAr, Bucket(0), Bucket(1), Bucket(2), Bucket(3), Bucket(4), mine.Sum(x => x.Open) - credit,
                k.CreditLimit, -credit);
        }).OrderByDescending(r => r.Total).ToList();
        var ledger = await finance.GetRoleBalanceAsync(AccountRoles.AccountsReceivable, date, ct);
        return new CustomerAgingReport(date, rows, rows.Sum(r => r.Total), ledger);
    }

    /// <summary>Post-dated cheques in hand by maturity, next to the PDC receivable account balance.</summary>
    private static async Task<PdcReport> PdcAsync(ErpDbContext db, ITenantContext t, IFinanceQueries finance, IClock clock, CancellationToken ct,
        DateOnly? dueBy = null)
    {
        var today = clock.Today;
        var q = from r in db.Set<CustomerReceipt>().AsNoTracking()
                join c in db.Set<Customer>() on r.CustomerId equals c.Id
                where r.CompanyId == t.RequireCompanyId() && r.Status == ReceiptStatus.PdcHeld
                select new { r.Id, r.ReceiptNo, r.CustomerId, c.NameEn, c.NameAr, r.ChequeNo, r.ChequeBank, ChequeDate = r.ChequeDate!.Value, r.Amount };
        if (dueBy is { } due) q = q.Where(x => x.ChequeDate <= due);
        var rows = (await q.OrderBy(x => x.ChequeDate).ThenBy(x => x.ReceiptNo).ToListAsync(ct))
            .Select(x => new PdcRow(x.Id, x.ReceiptNo, x.CustomerId, x.NameEn, x.NameAr, x.ChequeNo, x.ChequeBank, x.ChequeDate, x.Amount,
                x.ChequeDate.DayNumber - today.DayNumber)).ToList();
        var ledger = await finance.GetRoleBalanceAsync(AccountRoles.PdcReceivable, today.AddYears(10), ct);
        return new PdcReport(rows, rows.Sum(r => r.Amount), ledger);
    }

    /// <summary>Goods delivered but not yet invoiced, valued at the order price.</summary>
    private static async Task<List<UninvoicedRow>> UninvoicedAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct)
    {
        var rows = await (from l in db.Set<DeliveryNoteLine>().AsNoTracking()
                          join d in db.Set<DeliveryNote>() on l.DeliveryNoteId equals d.Id
                          join s in db.Set<SalesOrderLine>() on l.SalesOrderLineId equals s.Id
                          join c in db.Set<Customer>() on d.CustomerId equals c.Id
                          where d.CompanyId == t.RequireCompanyId() && d.Status == DocumentStatus.Posted && l.QuantityInvoiced < l.Quantity
                          orderby d.DeliveryDate, d.DnNo, l.LineNo
                          select new { d.Id, d.DnNo, d.DeliveryDate, d.CustomerId, c.NameEn, c.NameAr, l.ItemId, l.Quantity, l.QuantityInvoiced, s.Amount,
                              OrderQuantity = s.Quantity }).ToListAsync(ct);
        var items = await inv.GetItemsAsync(rows.Select(r => r.ItemId), ct);
        return rows.Select(r =>
        {
            var open = r.Quantity - r.QuantityInvoiced;
            return new UninvoicedRow(r.Id, r.DnNo, r.DeliveryDate, r.CustomerId, r.NameEn, r.NameAr, items[r.ItemId].Sku, items[r.ItemId].NameEn,
                items[r.ItemId].NameAr, open, Money.Round(r.Amount * open / r.OrderQuantity));
        }).ToList();
    }

    /// <summary>Net revenue (invoices less credit notes), cost and margin for a period, by item or by customer.</summary>
    private static async Task<SalesAnalysisReport> AnalysisAsync(ErpDbContext db, ITenantContext t, IInventoryQueries inv, IClock clock, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string groupBy = "item")
    {
        var end = to ?? clock.Today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        var companyId = t.RequireCompanyId();
        var byCustomer = string.Equals(groupBy, "customer", StringComparison.OrdinalIgnoreCase);
        var sold = await (from l in db.Set<SalesInvoiceLine>().AsNoTracking()
                          join i in db.Set<SalesInvoice>() on l.SalesInvoiceId equals i.Id
                          where i.CompanyId == companyId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Reversed && !i.IsOpening
                                && i.InvoiceDate >= start && i.InvoiceDate <= end
                          select new { i.CustomerId, l.ItemId, l.Quantity, l.Amount, l.Cost }).ToListAsync(ct);
        var returned = await (from l in db.Set<CreditNoteLine>().AsNoTracking()
                              join n in db.Set<CreditNote>() on l.CreditNoteId equals n.Id
                              where n.CompanyId == companyId && n.Status == DocumentStatus.Posted && n.NoteDate >= start && n.NoteDate <= end
                              select new { n.CustomerId, l.ItemId, l.LineType, l.Quantity, l.Amount, l.Cost }).ToListAsync(ct);

        var facts = sold.Select(s => (Key: byCustomer ? s.CustomerId : s.ItemId, s.Quantity, Revenue: s.Amount, Returns: 0m, s.Cost))
            .Concat(returned.Select(r => (Key: byCustomer ? r.CustomerId : r.ItemId,
                Quantity: r.LineType == CreditNoteLineType.Return ? -r.Quantity : 0m, Revenue: 0m, Returns: r.Amount, Cost: -r.Cost)))
            .ToList();
        var keys = facts.Where(f => f.Key is not null).Select(f => f.Key!.Value).Distinct().ToList();
        Dictionary<Guid, (string Code, string En, string Ar)> names = byCustomer
            ? (await CustomersAsync(db, keys, ct)).ToDictionary(x => x.Key, x => (x.Value.Code, x.Value.NameEn, x.Value.NameAr))
            : (await inv.GetItemsAsync(keys, ct)).ToDictionary(x => x.Key, x => (x.Value.Sku, x.Value.NameEn, x.Value.NameAr));
        var rows = facts.GroupBy(f => f.Key).Select(g =>
        {
            var (code, en, ar) = g.Key is { } k ? names[k] : ("—", "Services without an item", "خدمات بدون صنف");
            var revenue = g.Sum(x => x.Revenue);
            var returns = g.Sum(x => x.Returns);
            var cost = g.Sum(x => x.Cost);
            var net = revenue - returns;
            return new SalesAnalysisRow(g.Key, code, en, ar, g.Sum(x => x.Quantity), revenue, returns, net, cost, net - cost,
                net == 0 ? null : Math.Round((net - cost) * 100m / net, 2));
        }).OrderByDescending(r => r.NetRevenue).ToList();
        return new SalesAnalysisReport(start, end, byCustomer ? "customer" : "item", rows, rows.Sum(r => r.NetRevenue), rows.Sum(r => r.Cost),
            rows.Sum(r => r.Margin));
    }
}

/// <summary>Unapplied customer credits as of a date (documents dated on or before it).</summary>
internal sealed class CustomerCreditServiceQueries(ErpDbContext db, ITenantContext t)
{
    public sealed record Credit(Guid CustomerId, string Code, string NameEn, string NameAr, decimal? CreditLimit, decimal Amount);

    public async Task<List<Credit>> UnappliedAsync(DateOnly asOf, CancellationToken ct)
    {
        var companyId = t.RequireCompanyId();
        var receipts = await (from r in db.Set<CustomerReceipt>().AsNoTracking()
                              join c in db.Set<Customer>() on r.CustomerId equals c.Id
                              where r.CompanyId == companyId && r.UnappliedAmount > 0 && r.ReceiptDate <= asOf
                                    && (r.Status == ReceiptStatus.Posted || r.Status == ReceiptStatus.PdcHeld || r.Status == ReceiptStatus.PdcCleared)
                              select new Credit(c.Id, c.Code, c.NameEn, c.NameAr, c.CreditLimit, r.UnappliedAmount)).ToListAsync(ct);
        var notes = await (from n in db.Set<CreditNote>().AsNoTracking()
                           join c in db.Set<Customer>() on n.CustomerId equals c.Id
                           where n.CompanyId == companyId && n.UnappliedAmount > 0 && n.NoteDate <= asOf && n.Status == DocumentStatus.Posted
                           select new Credit(c.Id, c.Code, c.NameEn, c.NameAr, c.CreditLimit, n.UnappliedAmount)).ToListAsync(ct);
        return [.. receipts, .. notes];
    }
}

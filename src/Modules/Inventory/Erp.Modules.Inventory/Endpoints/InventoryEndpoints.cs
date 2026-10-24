using Erp.Infrastructure.Import;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Documents;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Endpoints;

public sealed record MasterDto(Guid Id, string Code, string NameEn, string NameAr, bool IsActive, Guid? BranchId = null);

public sealed record ItemDto(Guid Id, string Sku, string NameEn, string NameAr, string ItemType, Guid ItemGroupId, Guid UomId, string UomCode,
    string? Barcode, decimal? ReorderLevel, decimal? PurchasePrice, decimal? SalesPrice, string? HsCode, bool IsActive,
    decimal QuantityOnHand, decimal AverageCost, decimal StockValue, bool TrackBatches = false, bool TrackSerials = false, decimal? ReorderQuantity = null,
    string? Brand = null);

public sealed record BatchRow(Guid BatchId, Guid ItemId, string Sku, string NameEn, string NameAr, string BatchNo, DateOnly? ExpiryDate, Guid WarehouseId,
    string WarehouseCode, decimal Quantity, int? DaysLeft);

/// <summary><see cref="Boxes"/>: the item's other units (a box of 12), so the quantity can be shown as boxes and pieces.</summary>
public sealed record StockRow(Guid ItemId, string Sku, string NameEn, string NameAr, string UomCode, Guid ItemGroupId, decimal Quantity,
    decimal AverageCost, decimal Value, decimal? ReorderLevel, bool BelowReorder, IReadOnlyList<UnitSize>? Boxes = null);

public sealed record UnitSize(string UomCode, decimal Factor);

public sealed record ValuationRow(Guid ItemId, string Sku, string NameEn, string NameAr, decimal Quantity, decimal Value);

public sealed record ValuationReport(DateOnly AsOf, IReadOnlyList<ValuationRow> Rows, decimal TotalValue, decimal LedgerBalance, decimal Difference);

public sealed record LedgerRow(Guid Id, DateOnly Date, string SourceType, string SourceNo, Guid SourceId, Guid? WarehouseId, decimal Quantity,
    decimal UnitCost, decimal Value, decimal QuantityAfter, decimal ValueAfter, bool IsReversal);

public sealed record ItemLedger(Guid ItemId, DateOnly From, DateOnly To, decimal OpeningQuantity, decimal OpeningValue, IReadOnlyList<LedgerRow> Rows);

public sealed record AdjustmentLineDto(int LineNo, Guid ItemId, string Sku, string NameEn, string NameAr, decimal Quantity, decimal? UnitCost, decimal Value);

public sealed record AdjustmentDto(Guid Id, string AdjustmentNo, Guid WarehouseId, DateOnly AdjustmentDate, string Reason, string Status, string? Notes,
    decimal TotalValue, Guid? JournalEntryId, Guid? StockCountId, IReadOnlyList<AdjustmentLineDto> Lines);

public sealed record TransferLineDto(int LineNo, Guid ItemId, string Sku, string NameEn, string NameAr, decimal Quantity, decimal Value);

public sealed record TransferDto(Guid Id, string TransferNo, Guid FromWarehouseId, Guid ToWarehouseId, DateOnly TransferDate, string Status,
    string? Notes, IReadOnlyList<TransferLineDto> Lines);

public sealed record CountLineDto(Guid ItemId, string Sku, string NameEn, string NameAr, string UomCode, decimal SystemQuantity, decimal? CountedQuantity,
    decimal? Variance);

public sealed record CountDto(Guid Id, string CountNo, Guid WarehouseId, DateOnly CountDate, string Status, string? Notes, Guid? StockAdjustmentId,
    int LineCount, int CountedLines, IReadOnlyList<CountLineDto> Lines);

public sealed record InventorySettingsDto(bool AllowNegativeStock);

internal static class InventoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/inventory").WithTags("Inventory").RequireAuthorization();

        g.MapGet("/settings", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            new InventorySettingsDto(await db.Set<InventorySettings>().Where(s => s.CompanyId == t.RequireCompanyId()).Select(s => s.AllowNegativeStock).SingleAsync(ct)))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPut("/settings", async (InventorySettingsDto r, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var s = await db.Set<InventorySettings>().SingleAsync(x => x.CompanyId == t.RequireCompanyId(), ct);
            s.AllowNegativeStock = r.AllowNegativeStock;
            await db.SaveChangesAsync(ct);
            return r;
        }).RequirePermission(InventoryPermissions.SettingsManage);

        g.MapGet("/uoms", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await db.Set<Uom>().AsNoTracking().Where(u => u.CompanyId == t.RequireCompanyId()).OrderBy(u => u.Code).ToListAsync(ct))
            .Select(u => new MasterDto(u.Id, u.Code, u.NameEn, u.NameAr, true))).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/uoms", async (MasterRequest r, InventoryMasterService s, CancellationToken ct) =>
        {
            var u = await s.CreateUomAsync(r, ct);
            return new MasterDto(u.Id, u.Code, u.NameEn, u.NameAr, true);
        }).RequirePermission(InventoryPermissions.ItemsManage);

        g.MapGet("/item-groups", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await db.Set<ItemGroup>().AsNoTracking().Where(x => x.CompanyId == t.RequireCompanyId()).OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => new MasterDto(x.Id, x.Code, x.NameEn, x.NameAr, x.IsActive))).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/item-groups", async (MasterRequest r, InventoryMasterService s, CancellationToken ct) => Group(await s.SaveGroupAsync(null, r, ct)))
            .RequirePermission(InventoryPermissions.ItemsManage);
        g.MapPut("/item-groups/{id:guid}", async (Guid id, MasterRequest r, InventoryMasterService s, CancellationToken ct) => Group(await s.SaveGroupAsync(id, r, ct)))
            .RequirePermission(InventoryPermissions.ItemsManage);

        g.MapGet("/warehouses", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await db.Set<Warehouse>().AsNoTracking().Where(x => x.CompanyId == t.RequireCompanyId()).OrderBy(x => x.Code).ToListAsync(ct))
            .Select(Wh)).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/warehouses", async (MasterRequest r, InventoryMasterService s, CancellationToken ct) => Wh(await s.SaveWarehouseAsync(null, r, ct)))
            .RequirePermission(InventoryPermissions.SettingsManage);
        g.MapPut("/warehouses/{id:guid}", async (Guid id, MasterRequest r, InventoryMasterService s, CancellationToken ct) => Wh(await s.SaveWarehouseAsync(id, r, ct)))
            .RequirePermission(InventoryPermissions.SettingsManage);

        g.MapGet("/items", ListItems).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/imports/items/template", () => Results.File(ItemImportService.Template(), ExcelImport.ContentType, "items_import_template.xlsx"))
            .RequirePermission(InventoryPermissions.ItemsManage);
        g.MapPost("/imports/items", async (IFormFile file, ItemImportService s, CancellationToken ct, bool dryRun = true) =>
        {
            if (file.Length > ExcelImport.MaxFileBytes)
                throw new BusinessRuleException("import.too_large", "The file is larger than 10 MB.", "حجم الملف أكبر من 10 ميغابايت.");
            await using var stream = file.OpenReadStream();
            return await s.ImportAsync(stream, dryRun, ct);
        }).RequirePermission(InventoryPermissions.ItemsManage).DisableAntiforgery();
        g.MapGet("/items/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await ItemsQuery(db, t, q => q.Where(x => x.Id == id)).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Item", id))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/items", async (ItemRequest r, InventoryMasterService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var item = await s.SaveItemAsync(null, r, ct);
            return await ItemsQuery(db, t, q => q.Where(x => x.Id == item.Id)).SingleAsync(ct);
        }).RequirePermission(InventoryPermissions.ItemsManage);
        g.MapPut("/items/{id:guid}", async (Guid id, ItemRequest r, InventoryMasterService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            await s.SaveItemAsync(id, r, ct);
            return await ItemsQuery(db, t, q => q.Where(x => x.Id == id)).SingleAsync(ct);
        }).RequirePermission(InventoryPermissions.ItemsManage);
        g.MapGet("/items/{id:guid}/ledger", ItemLedgerAsync).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/items/{id:guid}/units", (Guid id, IInventoryQueries q, CancellationToken ct) => q.GetUnitsAsync([id], ct))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPut("/items/{id:guid}/units", async (Guid id, List<ItemUnitRequest> r, InventoryMasterService s, IInventoryQueries q, CancellationToken ct) =>
        {
            await s.SaveUnitsAsync(id, r, ct);
            return await q.GetUnitsAsync([id], ct);
        }).RequirePermission(InventoryPermissions.ItemsManage);

        g.MapGet("/items/{id:guid}/parts", (Guid id, InventoryMasterService s, CancellationToken ct) => s.GetPartsAsync(id, ct))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPut("/items/{id:guid}/parts", async (Guid id, ItemPartsRequest r, InventoryMasterService s, CancellationToken ct) =>
        {
            await s.SavePartsAsync(id, r, ct);
            return await s.GetPartsAsync(id, ct);
        }).RequirePermission(InventoryPermissions.ItemsManage);

        g.MapGet("/stock", StockAsync).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/reports/valuation", ValuationAsync).RequirePermission(InventoryPermissions.ReportsRead);
        g.MapGet("/batches", (ErpDbContext db, ITenantContext t, IClock clock, CancellationToken ct, Guid? itemId = null, Guid? warehouseId = null) =>
            BatchesAsync(db, t, clock, itemId, warehouseId, null, ct)).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/reports/expiry", (ErpDbContext db, ITenantContext t, IClock clock, CancellationToken ct, int withinDays = 90, Guid? warehouseId = null) =>
            BatchesAsync(db, t, clock, null, warehouseId, Math.Clamp(withinDays, 0, 3650), ct)).RequirePermission(InventoryPermissions.ItemsRead);

        g.MapGet("/adjustments", async (ErpDbContext db, ITenantContext t, CancellationToken ct) => await AdjustmentsAsync(db, t, null, ct))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/adjustments/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await AdjustmentsAsync(db, t, id, ct)).SingleOrDefault() ?? throw new NotFoundException("Stock adjustment", id))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/adjustments", async (AdjustmentRequest r, StockAdjustmentService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var a = await s.CreateAsync(r, ct);
            return (await AdjustmentsAsync(db, t, a.Id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);
        g.MapPost("/adjustments/{id:guid}/reverse", async (Guid id, ReverseRequest r, StockAdjustmentService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await AdjustmentsAsync(db, t, id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);

        g.MapGet("/transfers", async (ErpDbContext db, ITenantContext t, CancellationToken ct) => await TransfersAsync(db, t, null, ct))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/transfers/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await TransfersAsync(db, t, id, ct)).SingleOrDefault() ?? throw new NotFoundException("Stock transfer", id))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/transfers", async (TransferRequest r, StockTransferService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var x = await s.CreateAsync(r, ct);
            return (await TransfersAsync(db, t, x.Id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);
        g.MapPost("/transfers/{id:guid}/reverse", async (Guid id, ReverseRequest r, StockTransferService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            await s.ReverseAsync(id, r, ct);
            return (await TransfersAsync(db, t, id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);

        g.MapGet("/counts", async (ErpDbContext db, ITenantContext t, CancellationToken ct) => await CountsAsync(db, t, null, ct))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapGet("/counts/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await CountsAsync(db, t, id, ct)).SingleOrDefault() ?? throw new NotFoundException("Stock count", id))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/counts", async (CountRequest r, StockCountService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var c = await s.OpenAsync(r, ct);
            return (await CountsAsync(db, t, c.Id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);
        g.MapPut("/counts/{id:guid}/lines", async (Guid id, List<CountEntry> r, StockCountService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            await s.EnterAsync(id, r, ct);
            return (await CountsAsync(db, t, id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);
        g.MapPost("/counts/{id:guid}/post", async (Guid id, StockCountService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            await s.PostAsync(id, ct);
            return (await CountsAsync(db, t, id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);
        g.MapPost("/counts/{id:guid}/cancel", async (Guid id, StockCountService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            await s.CancelAsync(id, ct);
            return (await CountsAsync(db, t, id, ct)).Single();
        }).RequirePermission(InventoryPermissions.StockPost);
    }

    private static MasterDto Group(ItemGroup x) => new(x.Id, x.Code, x.NameEn, x.NameAr, x.IsActive);

    /// <summary>Batches with stock, soonest expiry first; with <paramref name="withinDays"/> only those expiring by then (expired included).</summary>
    internal static async Task<List<BatchRow>> BatchesAsync(ErpDbContext db, ITenantContext t, IClock clock, Guid? itemId, Guid? warehouseId, int? withinDays,
        CancellationToken ct)
    {
        var today = clock.Today;
        var limit = withinDays is { } d ? today.AddDays(d) : (DateOnly?)null;
        var rows = await (from s in db.Set<BatchStock>().AsNoTracking()
                          join b in db.Set<Batch>() on s.BatchId equals b.Id
                          join i in db.Set<Item>() on b.ItemId equals i.Id
                          join w in db.Set<Warehouse>() on s.WarehouseId equals w.Id
                          where b.CompanyId == t.RequireCompanyId() && s.Quantity != 0
                                && (itemId == null || b.ItemId == itemId) && (warehouseId == null || s.WarehouseId == warehouseId)
                                && (limit == null || (b.ExpiryDate != null && b.ExpiryDate <= limit))
                          select new { b, i, w, s.Quantity }).ToListAsync(ct);
        return rows
            .Select(x => new BatchRow(x.b.Id, x.i.Id, x.i.Sku, x.i.NameEn, x.i.NameAr, x.b.BatchNo, x.b.ExpiryDate, x.w.Id, x.w.Code, x.Quantity,
                x.b.ExpiryDate is { } e ? e.DayNumber - today.DayNumber : null))
            .OrderBy(x => x.ExpiryDate ?? DateOnly.MaxValue).ThenBy(x => x.Sku).ThenBy(x => x.BatchNo).ToList();
    }

    private static MasterDto Wh(Warehouse x) => new(x.Id, x.Code, x.NameEn, x.NameAr, x.IsActive, x.BranchId);

    private static IQueryable<ItemDto> ItemsQuery(ErpDbContext db, ITenantContext t, Func<IQueryable<Item>, IQueryable<Item>>? filter = null) =>
        from i in (filter ?? (q => q))(db.Set<Item>().AsNoTracking().Where(x => x.CompanyId == t.RequireCompanyId()))
        join u in db.Set<Uom>() on i.UomId equals u.Id
        join v in db.Set<ItemValuation>() on i.Id equals v.ItemId into vals
        from v in vals.DefaultIfEmpty()
        orderby i.Sku
        select new ItemDto(i.Id, i.Sku, i.NameEn, i.NameAr, i.ItemType.ToString(), i.ItemGroupId, i.UomId, u.Code, i.Barcode, i.ReorderLevel,
            i.PurchasePrice, i.SalesPrice, i.HsCode, i.IsActive, v == null ? 0 : v.Quantity, v == null ? 0 : v.AverageCost, v == null ? 0 : v.Value,
            i.TrackBatches, i.TrackSerials, i.ReorderQuantity, i.Brand);

    private static async Task<PagedResult<ItemDto>> ListItems(ErpDbContext db, ITenantContext t, CancellationToken ct, string? search = null,
        string? type = null, Guid? groupId = null, bool? active = null, int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        IQueryable<Item> Filter(IQueryable<Item> q)
        {
            // Every word must match something: the code, a name, the brand, a barcode, another part number, or a car the part fits
            // ("corolla 2015 filter": a filter that fits a 2015 Corolla).
            foreach (var word in (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var key = PartNumbers.Key(word);
                int? year = int.TryParse(word, out var y) && y is >= 1950 and <= 2100 ? y : null;
                q = q.Where(i => EF.Functions.ILike(i.Sku, $"%{word}%") || EF.Functions.ILike(i.NameEn, $"%{word}%") || i.NameAr.Contains(word)
                                 || i.Barcode == word || EF.Functions.ILike(i.Brand!, $"%{word}%")
                                 || db.Set<ItemUnit>().Any(u => u.ItemId == i.Id && u.Barcode == word)
                                 || (key != "" && db.Set<ItemPartNumber>().Any(p => p.ItemId == i.Id && p.PartKey.Contains(key)))
                                 || db.Set<ItemFitment>().Any(f => f.ItemId == i.Id && (EF.Functions.ILike(f.Make, $"%{word}%") || EF.Functions.ILike(f.Model, $"%{word}%")
                                     || EF.Functions.ILike(f.Engine!, $"%{word}%")
                                     || (year != null && (f.YearFrom == null || f.YearFrom <= year) && (f.YearTo == null || f.YearTo >= year)))));
            }
            if (Enum.TryParse<ItemType>(type, true, out var it)) q = q.Where(i => i.ItemType == it);
            if (groupId is not null) q = q.Where(i => i.ItemGroupId == groupId);
            if (active is not null) q = q.Where(i => i.IsActive == active);
            return q;
        }
        var total = await Filter(db.Set<Item>().AsNoTracking().Where(i => i.CompanyId == t.RequireCompanyId())).CountAsync(ct);
        var items = await ItemsQuery(db, t, q => Filter(q).OrderBy(i => i.Sku).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return new PagedResult<ItemDto>(items, total, page, pageSize);
    }

    internal static async Task<List<StockRow>> StockAsync(ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? warehouseId = null,
        string? search = null, bool belowReorder = false)
    {
        var companyId = t.RequireCompanyId();
        var rows = await (from i in db.Set<Item>().AsNoTracking()
                          join u in db.Set<Uom>() on i.UomId equals u.Id
                          join v in db.Set<ItemValuation>() on i.Id equals v.ItemId
                          where i.CompanyId == companyId && i.ItemType == ItemType.Stock
                          select new
                          {
                              i.Id, i.Sku, i.NameEn, i.NameAr, u.Code, i.ItemGroupId, i.ReorderLevel, i.IsActive, v.Quantity, v.Value, v.AverageCost,
                              WarehouseQuantity = warehouseId == null
                                  ? v.Quantity
                                  : db.Set<WarehouseStock>().Where(s => s.ItemId == i.Id && s.WarehouseId == warehouseId).Select(s => s.Quantity).FirstOrDefault()
                          }).ToListAsync(ct);
        var term = search?.Trim();
        var ids = rows.Select(r => r.Id).ToList();
        var boxes = (await (from x in db.Set<ItemUnit>().AsNoTracking()
                            join u in db.Set<Uom>() on x.UomId equals u.Id
                            where ids.Contains(x.ItemId)
                            select new { x.ItemId, u.Code, x.Factor }).ToListAsync(ct))
            .ToLookup(x => x.ItemId, x => new UnitSize(x.Code, x.Factor));
        return rows
            .Where(r => r.IsActive || r.WarehouseQuantity != 0)
            .Where(r => string.IsNullOrEmpty(term) || r.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.NameEn.Contains(term, StringComparison.OrdinalIgnoreCase) || r.NameAr.Contains(term))
            .Select(r =>
            {
                var value = warehouseId is null ? r.Value : Money.Round(r.WarehouseQuantity * r.AverageCost);
                var below = r.ReorderLevel is { } level && r.Quantity <= level;
                return new StockRow(r.Id, r.Sku, r.NameEn, r.NameAr, r.Code, r.ItemGroupId, r.WarehouseQuantity, r.AverageCost, value, r.ReorderLevel, below,
                    boxes[r.Id].Any() ? boxes[r.Id].ToList() : null);
            })
            .Where(r => !belowReorder || r.BelowReorder)
            .OrderBy(r => r.Sku).ToList();
    }

    /// <summary>Stock value from the movement ledger at a date, next to the Inventory account balance (they must agree).</summary>
    private static async Task<ValuationReport> ValuationAsync(ErpDbContext db, ITenantContext t, IFinanceQueries finance, CancellationToken ct, DateOnly? asOf = null)
    {
        var date = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var companyId = t.RequireCompanyId();
        var rows = await (from m in db.Set<StockMovement>().AsNoTracking()
                          join i in db.Set<Item>() on m.ItemId equals i.Id
                          where m.CompanyId == companyId && m.MovementDate <= date
                          group m by new { i.Id, i.Sku, i.NameEn, i.NameAr } into g
                          select new ValuationRow(g.Key.Id, g.Key.Sku, g.Key.NameEn, g.Key.NameAr, g.Sum(m => m.Quantity), g.Sum(m => m.Value)))
            .ToListAsync(ct);
        rows = rows.Where(r => r.Quantity != 0 || r.Value != 0).OrderBy(r => r.Sku).ToList();
        var total = rows.Sum(r => r.Value);
        var ledger = await finance.GetRoleBalanceAsync(AccountRoles.Inventory, date, ct);
        return new ValuationReport(date, rows, total, ledger, total - ledger);
    }

    private static async Task<ItemLedger> ItemLedgerAsync(Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct, DateOnly? from = null,
        DateOnly? to = null, Guid? warehouseId = null)
    {
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(end.Year, 1, 1);
        var q = db.Set<StockMovement>().AsNoTracking().Where(m => m.ItemId == id && m.CompanyId == t.RequireCompanyId());
        if (warehouseId is not null) q = q.Where(m => m.WarehouseId == warehouseId);
        var opening = await q.Where(m => m.MovementDate < start).GroupBy(_ => 1)
            .Select(g => new { Quantity = g.Sum(m => m.Quantity), Value = g.Sum(m => m.Value) }).SingleOrDefaultAsync(ct);
        var movements = await q.Where(m => m.MovementDate >= start && m.MovementDate <= end)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct);

        decimal quantity = opening?.Quantity ?? 0, value = opening?.Value ?? 0;
        var rows = new List<LedgerRow>();
        foreach (var m in movements)
        {
            quantity += m.Quantity;
            value += m.Value;
            rows.Add(new LedgerRow(m.Id, m.MovementDate, m.SourceType, m.SourceNo, m.SourceId, m.WarehouseId, m.Quantity, m.UnitCost, m.Value,
                quantity, value, m.ReversalOfId is not null));
        }
        return new ItemLedger(id, start, end, opening?.Quantity ?? 0, opening?.Value ?? 0, rows);
    }

    private static async Task<List<AdjustmentDto>> AdjustmentsAsync(ErpDbContext db, ITenantContext t, Guid? id, CancellationToken ct)
    {
        var q = db.Set<StockAdjustment>().AsNoTracking().Include(a => a.Lines).Where(a => a.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(a => a.Id == id);
        var list = await q.OrderByDescending(a => a.AdjustmentDate).ThenByDescending(a => a.AdjustmentNo).Take(500).ToListAsync(ct);
        var items = await ItemNamesAsync(db, list.SelectMany(a => a.Lines.Select(l => l.ItemId)), ct);
        return list.Select(a => new AdjustmentDto(a.Id, a.AdjustmentNo, a.WarehouseId, a.AdjustmentDate, a.Reason.ToString(), a.Status.ToString(), a.Notes,
            a.TotalValue, a.JournalEntryId, a.StockCountId,
            a.Lines.OrderBy(l => l.LineNo).Select(l => new AdjustmentLineDto(l.LineNo, l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn,
                items[l.ItemId].NameAr, l.Quantity, l.UnitCost, l.Value)).ToList())).ToList();
    }

    private static async Task<List<TransferDto>> TransfersAsync(ErpDbContext db, ITenantContext t, Guid? id, CancellationToken ct)
    {
        var q = db.Set<StockTransfer>().AsNoTracking().Include(a => a.Lines).Where(a => a.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(a => a.Id == id);
        var list = await q.OrderByDescending(a => a.TransferDate).ThenByDescending(a => a.TransferNo).Take(500).ToListAsync(ct);
        var items = await ItemNamesAsync(db, list.SelectMany(a => a.Lines.Select(l => l.ItemId)), ct);
        return list.Select(x => new TransferDto(x.Id, x.TransferNo, x.FromWarehouseId, x.ToWarehouseId, x.TransferDate, x.Status.ToString(), x.Notes,
            x.Lines.OrderBy(l => l.LineNo).Select(l => new TransferLineDto(l.LineNo, l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn,
                items[l.ItemId].NameAr, l.Quantity, l.Value)).ToList())).ToList();
    }

    private static async Task<List<CountDto>> CountsAsync(ErpDbContext db, ITenantContext t, Guid? id, CancellationToken ct)
    {
        var q = db.Set<StockCount>().AsNoTracking().Include(a => a.Lines).Where(a => a.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(a => a.Id == id);
        var list = await q.OrderByDescending(a => a.CountDate).ThenByDescending(a => a.CountNo).Take(200).ToListAsync(ct);
        var items = await ItemNamesAsync(db, id is null ? [] : list.SelectMany(a => a.Lines.Select(l => l.ItemId)), ct);
        return list.Select(c => new CountDto(c.Id, c.CountNo, c.WarehouseId, c.CountDate, c.Status.ToString(), c.Notes, c.StockAdjustmentId,
            c.Lines.Count, c.Lines.Count(l => l.CountedQuantity is not null),
            id is null ? [] : c.Lines.Select(l => new CountLineDto(l.ItemId, items[l.ItemId].Sku, items[l.ItemId].NameEn, items[l.ItemId].NameAr,
                items[l.ItemId].UomCode, l.SystemQuantity, l.CountedQuantity, l.CountedQuantity - l.SystemQuantity)).OrderBy(l => l.Sku).ToList())).ToList();
    }

    private static async Task<Dictionary<Guid, (string Sku, string NameEn, string NameAr, string UomCode)>> ItemNamesAsync(ErpDbContext db,
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        return (await (from i in db.Set<Item>().AsNoTracking()
                       join u in db.Set<Uom>() on i.UomId equals u.Id
                       where list.Contains(i.Id)
                       select new { i.Id, i.Sku, i.NameEn, i.NameAr, u.Code }).ToListAsync(ct))
            .ToDictionary(i => i.Id, i => (i.Sku, i.NameEn, i.NameAr, i.Code));
    }
}

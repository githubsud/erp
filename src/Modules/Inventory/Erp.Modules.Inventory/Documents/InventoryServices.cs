using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Documents;

public sealed record MasterRequest(string Code, string? NameEn, string? NameAr, bool IsActive = true, Guid? BranchId = null);

public sealed record ItemRequest(string Sku, string? NameEn, string? NameAr, ItemType ItemType, Guid ItemGroupId, Guid UomId, string? Barcode,
    decimal? ReorderLevel, decimal? PurchasePrice, decimal? SalesPrice, string? HsCode, bool IsActive = true);

public sealed record AdjustmentLineRequest(Guid ItemId, decimal Quantity, decimal? UnitCost);

public sealed record AdjustmentRequest(Guid WarehouseId, DateOnly Date, AdjustmentReason Reason, string? Notes, IReadOnlyList<AdjustmentLineRequest> Lines);

public sealed record TransferLineRequest(Guid ItemId, decimal Quantity);

public sealed record TransferRequest(Guid FromWarehouseId, Guid ToWarehouseId, DateOnly Date, string? Notes, IReadOnlyList<TransferLineRequest> Lines);

public sealed record ReverseRequest(DateOnly Date, string Reason);

public sealed record CountRequest(Guid WarehouseId, DateOnly Date, string? Notes, Guid? ItemGroupId);

public sealed record CountEntry(Guid ItemId, decimal? CountedQuantity);

/// <summary>Units, item groups, warehouses and items.</summary>
public sealed record ItemUnitRequest(Guid UomId, decimal Factor);

internal sealed class InventoryMasterService(ErpDbContext db, ITenantContext tenant)
{
    /// <summary>Replaces an item's alternative units. The base unit is implied; each unit appears once.</summary>
    public Task SaveUnitsAsync(Guid itemId, IReadOnlyList<ItemUnitRequest> units, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var item = await db.Set<Item>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.CompanyId == tenant.RequireCompanyId(), token)
                       ?? throw new NotFoundException("Item", itemId);
            if (units.Any(u => u.UomId == item.UomId))
                throw new BusinessRuleException("inv.unit_base", "The base unit is already part of the item.", "الوحدة الأساسية جزء من الصنف بالفعل.");
            if (units.Select(u => u.UomId).Distinct().Count() != units.Count || units.Any(u => u.Factor <= 0))
                throw new BusinessRuleException("inv.unit_factor", "List each unit once with a positive number of base units.",
                    "أدخل كل وحدة مرة واحدة مع عدد موجب من الوحدات الأساسية.");
            var ids = units.Select(u => u.UomId).ToList();
            var known = await db.Set<Uom>().Where(u => ids.Contains(u.Id) && u.CompanyId == item.CompanyId).CountAsync(token);
            if (known != ids.Count) throw new NotFoundException("Unit", string.Join(',', ids));
            db.RemoveRange(await db.Set<ItemUnit>().Where(u => u.ItemId == itemId).ToListAsync(token));
            await db.SaveChangesAsync(token);
            foreach (var u in units) db.Add(new ItemUnit { ItemId = itemId, UomId = u.UomId, Factor = Math.Round(u.Factor, 6, MidpointRounding.AwayFromZero) });
            await db.SaveChangesAsync(token);
        }, ct);

    public async Task<Uom> CreateUomAsync(MasterRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        var uom = new Uom { CompanyId = tenant.RequireCompanyId(), Code = Code(r.Code), NameEn = en, NameAr = ar };
        db.Add(uom);
        await db.SaveChangesAsync(ct);
        return uom;
    }

    public async Task<ItemGroup> SaveGroupAsync(Guid? id, MasterRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        var group = id is null
            ? db.Add(new ItemGroup { CompanyId = tenant.RequireCompanyId(), Code = Code(r.Code), NameEn = en, NameAr = ar }).Entity
            : await db.Set<ItemGroup>().SingleOrDefaultAsync(g => g.Id == id && g.CompanyId == tenant.RequireCompanyId(), ct)
              ?? throw new NotFoundException("Item group", id);
        group.Code = Code(r.Code);
        group.NameEn = en;
        group.NameAr = ar;
        group.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<Warehouse> SaveWarehouseAsync(Guid? id, MasterRequest r, CancellationToken ct)
    {
        var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
        var warehouse = id is null
            ? db.Add(new Warehouse { CompanyId = tenant.RequireCompanyId(), Code = Code(r.Code), NameEn = en, NameAr = ar }).Entity
            : await db.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == id && w.CompanyId == tenant.RequireCompanyId(), ct)
              ?? throw new NotFoundException("Warehouse", id);
        if (id is not null && !r.IsActive && await db.Set<WarehouseStock>().AnyAsync(s => s.WarehouseId == warehouse.Id && s.Quantity != 0, ct))
            throw new BusinessRuleException("inv.warehouse_has_stock", "A warehouse with stock cannot be deactivated.", "لا يمكن إيقاف مستودع به مخزون.");
        warehouse.Code = Code(r.Code);
        warehouse.NameEn = en;
        warehouse.NameAr = ar;
        warehouse.BranchId = r.BranchId;
        warehouse.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return warehouse;
    }

    public Task<Item> SaveItemAsync(Guid? id, ItemRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var (en, ar) = Bilingual.Require(r.NameEn, r.NameAr);
            if (!await db.Set<ItemGroup>().AnyAsync(g => g.Id == r.ItemGroupId && g.CompanyId == companyId, token))
                throw new NotFoundException("Item group", r.ItemGroupId);
            if (!await db.Set<Uom>().AnyAsync(u => u.Id == r.UomId && u.CompanyId == companyId, token))
                throw new NotFoundException("Unit", r.UomId);
            foreach (var (value, field) in new[] { (r.ReorderLevel, "reorderLevel"), (r.PurchasePrice, "purchasePrice"), (r.SalesPrice, "salesPrice") })
                if (value is { } v) Guard.NonNegative(v, field);

            Item item;
            if (id is null)
            {
                item = new Item { CompanyId = companyId, Sku = Code(r.Sku), NameEn = en, NameAr = ar };
                db.Add(item);
                db.Add(new ItemValuation { ItemId = item.Id });
            }
            else
            {
                item = await db.Set<Item>().SingleOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId, token) ?? throw new NotFoundException("Item", id);
                if (item.ItemType != r.ItemType && await db.Set<StockMovement>().AnyAsync(m => m.ItemId == item.Id, token))
                    throw new BusinessRuleException("inv.item_type_locked", "The item type cannot change once the item has stock movements.",
                        "لا يمكن تغيير نوع الصنف بعد وجود حركات مخزون عليه.");
            }
            item.Sku = Code(r.Sku);
            item.NameEn = en;
            item.NameAr = ar;
            item.ItemType = r.ItemType;
            item.ItemGroupId = r.ItemGroupId;
            item.UomId = r.UomId;
            item.Barcode = string.IsNullOrWhiteSpace(r.Barcode) ? null : r.Barcode.Trim();
            item.ReorderLevel = r.ReorderLevel;
            item.PurchasePrice = r.PurchasePrice;
            item.SalesPrice = r.SalesPrice;
            item.HsCode = string.IsNullOrWhiteSpace(r.HsCode) ? null : r.HsCode.Trim();
            item.IsActive = r.IsActive;
            await db.SaveChangesAsync(token);
            return item;
        }, ct);

    private static string Code(string? code) => Guard.Required(code, "code").ToUpperInvariant();
}

/// <summary>
/// Opening stock and adjustments. Gains debit Inventory and credit Stock Adjustments (Opening Balance Equity for opening
/// stock); losses the other way round, at the weighted-average cost.
/// </summary>
internal sealed class StockAdjustmentService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IStockService stock, IPostingService posting)
{
    public const string SourceType = "stock_adjustment";

    public Task<StockAdjustment> CreateAsync(AdjustmentRequest r, CancellationToken ct, Guid? stockCountId = null) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            if (r.Lines is not { Count: > 0 })
                throw new BusinessRuleException("inv.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");
            if (r.Lines.Any(l => l.Quantity == 0))
                throw new BusinessRuleException("inv.zero_quantity", "Quantities cannot be zero.", "لا يمكن أن تكون الكمية صفراً.");
            if (r.Reason == AdjustmentReason.Opening && r.Lines.Any(l => l.Quantity < 0 || l.UnitCost is null))
                throw new BusinessRuleException("inv.opening_lines", "Opening stock needs positive quantities and a unit cost.",
                    "يتطلب المخزون الافتتاحي كميات موجبة وتكلفة للوحدة.");
            if (r.Lines.Any(l => l.Quantity > 0 && l.UnitCost is null))
                throw new BusinessRuleException("inv.unit_cost", "Enter the unit cost for every quantity added.", "أدخل تكلفة الوحدة لكل كمية مضافة.");

            var adjustment = new StockAdjustment
            {
                CompanyId = companyId,
                AdjustmentNo = await numbering.NextAsync(SourceType, "ADJ", r.Date, ct: token),
                WarehouseId = r.WarehouseId,
                AdjustmentDate = r.Date,
                Reason = r.Reason,
                Notes = r.Notes,
                StockCountId = stockCountId
            };
            var lineNo = 0;
            foreach (var l in r.Lines)
                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    StockAdjustmentId = adjustment.Id, LineNo = ++lineNo, ItemId = l.ItemId, Quantity = l.Quantity,
                    UnitCost = l.Quantity > 0 ? l.UnitCost : null
                });
            db.Add(adjustment);

            var results = await stock.PostAsync(new StockPosting(SourceType, adjustment.Id, adjustment.AdjustmentNo, r.Date,
                adjustment.Lines.Select(l => new StockLine(l.ItemId, r.WarehouseId, l.Quantity, l.UnitCost, l.Id)).ToList()), token);
            foreach (var result in results) adjustment.Lines.Single(l => l.Id == result.SourceLineId).Value = result.Value;
            adjustment.TotalValue = results.Sum(x => x.Value);

            var offset = r.Reason == AdjustmentReason.Opening ? AccountRoles.OpeningBalanceEquity : AccountRoles.StockAdjustment;
            var lines = new List<PostingLine>();
            foreach (var g in results.GroupBy(x => x.ItemGroupId))
            {
                var net = g.Sum(x => x.Value);
                if (net == 0) continue;
                var inventorySide = net > 0 ? PostingSide.Debit : PostingSide.Credit;
                lines.Add(new PostingLine { AccountRole = AccountRoles.Inventory, RoleKey = g.Key, Side = inventorySide, Amount = Math.Abs(net) });
                lines.Add(new PostingLine
                {
                    AccountRole = offset, Side = inventorySide == PostingSide.Debit ? PostingSide.Credit : PostingSide.Debit, Amount = Math.Abs(net)
                });
            }
            if (lines.Count > 0)
            {
                var journal = await posting.PostAsync(new PostingRequest
                {
                    SourceType = SourceType,
                    SourceId = adjustment.Id,
                    SourceNo = adjustment.AdjustmentNo,
                    PostingDate = r.Date,
                    MemoEn = $"Stock adjustment ({r.Reason}) {adjustment.AdjustmentNo}",
                    MemoAr = $"تسوية مخزون {adjustment.AdjustmentNo}",
                    Lines = lines
                }, token);
                adjustment.JournalEntryId = journal.JournalEntryId;
            }
            await db.SaveChangesAsync(token);
            return adjustment;
        }, ct);

    public Task<StockAdjustment> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var adjustment = await db.Set<StockAdjustment>().Include(a => a.Lines)
                                 .SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), token)
                             ?? throw new NotFoundException("Stock adjustment", id);
            if (adjustment.Status != InventoryDocumentStatus.Posted)
                throw new ConflictException("inv.not_posted", "Only posted adjustments can be reversed.", "يمكن عكس التسويات المرحّلة فقط.");
            if (adjustment.StockCountId is not null)
                throw new BusinessRuleException("inv.count_adjustment", "Adjustments from a stock count cannot be reversed; count again instead.",
                    "لا يمكن عكس تسوية ناتجة عن جرد؛ قم بجرد جديد بدلاً من ذلك.");
            await stock.ReverseAsync(SourceType, adjustment.Id, r.Date, token);
            if (adjustment.JournalEntryId is not null) await posting.ReverseAsync(SourceType, adjustment.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            adjustment.Status = InventoryDocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return adjustment;
        }, ct);
}

/// <summary>Moves stock between warehouses at the average cost. Valuation is per company, so no journal entry is needed.</summary>
internal sealed class StockTransferService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IStockService stock)
{
    public const string SourceType = "stock_transfer";

    public Task<StockTransfer> CreateAsync(TransferRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.FromWarehouseId == r.ToWarehouseId)
                throw new BusinessRuleException("inv.same_warehouse", "Choose two different warehouses.", "اختر مستودعين مختلفين.");
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0))
                throw new BusinessRuleException("inv.transfer_lines", "Add lines with positive quantities.", "أضف أسطراً بكميات موجبة.");
            var transfer = new StockTransfer
            {
                CompanyId = tenant.RequireCompanyId(),
                TransferNo = await numbering.NextAsync(SourceType, "TRF", r.Date, ct: token),
                FromWarehouseId = r.FromWarehouseId,
                ToWarehouseId = r.ToWarehouseId,
                TransferDate = r.Date,
                Notes = r.Notes
            };
            var lineNo = 0;
            foreach (var l in r.Lines)
                transfer.Lines.Add(new StockTransferLine { StockTransferId = transfer.Id, LineNo = ++lineNo, ItemId = l.ItemId, Quantity = l.Quantity });
            db.Add(transfer);

            var issued = await stock.PostAsync(new StockPosting(SourceType, transfer.Id, transfer.TransferNo, r.Date,
                transfer.Lines.Select(l => new StockLine(l.ItemId, r.FromWarehouseId, -l.Quantity, SourceLineId: l.Id)).ToList()), token);
            await stock.PostAsync(new StockPosting(SourceType, transfer.Id, transfer.TransferNo, r.Date,
                issued.Select(x => new StockLine(x.ItemId, r.ToWarehouseId, -x.Quantity, SourceLineId: x.SourceLineId, Value: -x.Value)).ToList()), token);
            foreach (var x in issued) transfer.Lines.Single(l => l.Id == x.SourceLineId).Value = -x.Value;
            await db.SaveChangesAsync(token);
            return transfer;
        }, ct);

    public Task<StockTransfer> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var transfer = await db.Set<StockTransfer>().Include(t => t.Lines)
                               .SingleOrDefaultAsync(t => t.Id == id && t.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Stock transfer", id);
            if (transfer.Status != InventoryDocumentStatus.Posted)
                throw new ConflictException("inv.not_posted", "Only posted transfers can be reversed.", "يمكن عكس التحويلات المرحّلة فقط.");
            await stock.ReverseAsync(SourceType, transfer.Id, r.Date, token);
            transfer.Status = InventoryDocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return transfer;
        }, ct);
}

/// <summary>Stock counts: open (freezes system quantities), enter counts, post the variances as one adjustment.</summary>
internal sealed class StockCountService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, StockAdjustmentService adjustments)
{
    public const string SourceType = "stock_count";

    public Task<StockCount> OpenAsync(CountRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            if (!await db.Set<Warehouse>().AnyAsync(w => w.Id == r.WarehouseId && w.CompanyId == companyId && w.IsActive, token))
                throw new NotFoundException("Warehouse", r.WarehouseId);
            if (await db.Set<StockCount>().AnyAsync(c => c.WarehouseId == r.WarehouseId && c.Status == StockCountStatus.Open, token))
                throw new ConflictException("inv.count_open", "This warehouse already has an open stock count.", "يوجد جرد مفتوح لهذا المستودع.");

            var count = new StockCount
            {
                CompanyId = companyId,
                CountNo = await numbering.NextAsync(SourceType, "CNT", r.Date, ct: token),
                WarehouseId = r.WarehouseId,
                CountDate = r.Date,
                Notes = r.Notes
            };
            var items = await (from i in db.Set<Item>()
                               join s in db.Set<WarehouseStock>().Where(s => s.WarehouseId == r.WarehouseId) on i.Id equals s.ItemId into stock
                               from s in stock.DefaultIfEmpty()
                               where i.CompanyId == companyId && i.ItemType == ItemType.Stock && (i.IsActive || (s != null && s.Quantity != 0))
                                     && (r.ItemGroupId == null || i.ItemGroupId == r.ItemGroupId)
                               select new { i.Id, Quantity = s == null ? 0 : s.Quantity }).ToListAsync(token);
            foreach (var i in items)
                count.Lines.Add(new StockCountLine { StockCountId = count.Id, ItemId = i.Id, SystemQuantity = i.Quantity });
            db.Add(count);
            await db.SaveChangesAsync(token);
            return count;
        }, ct);

    public async Task<StockCount> EnterAsync(Guid id, IReadOnlyList<CountEntry> entries, CancellationToken ct)
    {
        var count = await OpenCountAsync(id, ct);
        foreach (var e in entries)
        {
            var line = count.Lines.SingleOrDefault(l => l.ItemId == e.ItemId) ?? throw new NotFoundException("Count line", e.ItemId);
            if (e.CountedQuantity is < 0)
                throw new BusinessRuleException("inv.count_negative", "Counted quantities cannot be negative.", "لا يمكن أن تكون الكمية المعدودة سالبة.");
            line.CountedQuantity = e.CountedQuantity;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    public Task<StockCount> PostAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var count = await OpenCountAsync(id, token);
            if (count.Lines.Any(l => l.CountedQuantity is null))
                throw new BusinessRuleException("inv.count_incomplete", "Enter a counted quantity for every line (0 if none found).",
                    "أدخل الكمية المعدودة لكل سطر (0 إن لم توجد).");
            var variances = count.Lines.Where(l => l.CountedQuantity != l.SystemQuantity).ToList();
            if (variances.Count > 0)
            {
                var averages = await db.Set<ItemValuation>().Where(v => variances.Select(l => l.ItemId).Contains(v.ItemId))
                    .ToDictionaryAsync(v => v.ItemId, v => v.AverageCost, token);
                var adjustment = await adjustments.CreateAsync(new AdjustmentRequest(count.WarehouseId, count.CountDate, AdjustmentReason.Count,
                    $"Stock count {count.CountNo}",
                    variances.Select(l => new AdjustmentLineRequest(l.ItemId, l.CountedQuantity!.Value - l.SystemQuantity,
                        averages.GetValueOrDefault(l.ItemId))).ToList()), token, count.Id);
                count.StockAdjustmentId = adjustment.Id;
            }
            count.Status = StockCountStatus.Posted;
            await db.SaveChangesAsync(token);
            return count;
        }, ct);

    public async Task<StockCount> CancelAsync(Guid id, CancellationToken ct)
    {
        var count = await OpenCountAsync(id, ct);
        count.Status = StockCountStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return count;
    }

    private async Task<StockCount> OpenCountAsync(Guid id, CancellationToken ct)
    {
        var count = await db.Set<StockCount>().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == tenant.RequireCompanyId(), ct)
                    ?? throw new NotFoundException("Stock count", id);
        if (count.Status != StockCountStatus.Open)
            throw new ConflictException("inv.count_closed", "This stock count is already closed.", "تم إغلاق هذا الجرد مسبقاً.");
        return count;
    }
}

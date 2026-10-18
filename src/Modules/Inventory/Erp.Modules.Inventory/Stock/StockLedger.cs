using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Stock;

/// <summary>
/// Weighted-average costing per item per company. Each item's valuation row is locked (<c>FOR UPDATE</c>) for the
/// rest of the transaction, so concurrent issues of the same item are serialised and negative-stock checks hold.
/// Receipts add quantity × cost; issues take the current average (the last unit takes whatever value remains, so
/// no residue is left behind); revaluations change value only. Every change writes an insert-only movement.
/// </summary>
internal sealed class StockLedger(ErpDbContext db, ITenantContext tenant) : IStockService
{
    public async Task<IReadOnlyList<StockLineResult>> PostAsync(StockPosting posting, CancellationToken ct = default)
    {
        db.RequireTransaction("Posting stock");
        if (posting.Lines.Count == 0) return [];
        var items = await LoadStockItemsAsync(posting.Lines.Select(l => l.ItemId), ct);
        var valuations = await LockAsync(items.Keys, ct);
        var allowNegative = await AllowNegativeAsync(ct);

        var results = new List<StockLineResult>();
        foreach (var line in posting.Lines)
        {
            var item = items[line.ItemId];
            var valuation = valuations[line.ItemId];
            var stock = await WarehouseStockAsync(line.ItemId, line.WarehouseId, ct);
            if (line.Quantity == 0)
                throw new BusinessRuleException("inv.zero_quantity", $"Quantity for {item.Sku} cannot be zero.", $"لا يمكن أن تكون الكمية صفراً للصنف {item.Sku}.");

            decimal value;
            if (line.Quantity > 0)
            {
                if (line.Value is null && line.UnitCost is not >= 0)
                    throw new BusinessRuleException("inv.unit_cost", $"A unit cost is needed to receive {item.Sku}.", $"يلزم تحديد تكلفة الوحدة لاستلام {item.Sku}.");
                value = line.Value ?? Money.Round(line.Quantity * line.UnitCost!.Value);
            }
            else
            {
                if (!allowNegative && stock.Quantity + line.Quantity < 0)
                    throw InsufficientStock(item, stock.Quantity, -line.Quantity);
                value = line.Value
                        ?? (line.UnitCost is { } cost ? -Money.Round(-line.Quantity * cost)
                            : valuation.Quantity + line.Quantity == 0 ? -valuation.Value
                            : -Money.Round(-line.Quantity * CurrentAverage(valuation)));
            }

            Apply(valuation, line.Quantity, value);
            stock.Quantity += line.Quantity;
            var movement = Movement(posting.SourceType, posting.SourceId, posting.SourceNo, posting.Date, item, line.WarehouseId, line.Quantity, value,
                valuation, line.SourceLineId, null);
            db.Add(movement);
            results.Add(new StockLineResult(item.Id, item.ItemGroupId, line.WarehouseId, line.SourceLineId, line.Quantity, value));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<IReadOnlyList<RevaluationResult>> RevalueAsync(string sourceType, Guid sourceId, string sourceNo, DateOnly date,
        IReadOnlyList<RevaluationLine> lines, CancellationToken ct = default)
    {
        db.RequireTransaction("Revaluing stock");
        if (lines.Count == 0) return [];
        var items = await LoadStockItemsAsync(lines.Select(l => l.ItemId), ct);
        var valuations = await LockAsync(items.Keys, ct);

        var results = new List<RevaluationResult>();
        foreach (var line in lines)
        {
            var item = items[line.ItemId];
            var valuation = valuations[line.ItemId];
            var amount = Money.Round(line.Amount);
            var share = line.ReceivedQuantity <= 0 ? 0m : Math.Clamp(valuation.Quantity / line.ReceivedQuantity, 0m, 1m);
            var toInventory = Money.Round(amount * share);
            if (toInventory != 0)
            {
                Apply(valuation, 0, toInventory);
                db.Add(Movement(sourceType, sourceId, sourceNo, date, item, null, 0, toInventory, valuation, line.SourceLineId, null));
            }
            results.Add(new RevaluationResult(item.Id, item.ItemGroupId, line.SourceLineId, toInventory, amount - toInventory));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<IReadOnlyList<StockLineResult>> ReverseAsync(string sourceType, Guid sourceId, DateOnly date, CancellationToken ct = default)
    {
        db.RequireTransaction("Reversing stock");
        var originals = await db.Set<StockMovement>().AsNoTracking()
            .Where(m => m.SourceType == sourceType && m.SourceId == sourceId && m.ReversalOfId == null)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct);
        if (originals.Count == 0) return [];
        var ids = originals.Select(m => m.Id).ToList();
        if (await db.Set<StockMovement>().AnyAsync(m => m.ReversalOfId != null && ids.Contains(m.ReversalOfId.Value), ct))
            throw new ConflictException("inv.already_reversed", "The stock movements of this document are already reversed.", "تم عكس حركات المخزون لهذا المستند مسبقاً.");

        var items = await LoadStockItemsAsync(originals.Select(m => m.ItemId), ct, allowInactive: true);
        var valuations = await LockAsync(items.Keys, ct);
        var allowNegative = await AllowNegativeAsync(ct);
        var results = new List<StockLineResult>();
        foreach (var original in Enumerable.Reverse(originals))
        {
            var item = items[original.ItemId];
            var valuation = valuations[original.ItemId];
            var quantity = -original.Quantity;
            if (original.WarehouseId is { } warehouseId)
            {
                var stock = await WarehouseStockAsync(original.ItemId, warehouseId, ct);
                if (quantity < 0 && !allowNegative && stock.Quantity + quantity < 0)
                    throw InsufficientStock(item, stock.Quantity, -quantity);
                stock.Quantity += quantity;
            }
            Apply(valuation, quantity, -original.Value);
            db.Add(Movement(original.SourceType, original.SourceId, original.SourceNo, date, item, original.WarehouseId, quantity, -original.Value,
                valuation, original.SourceLineId, original.Id));
            results.Add(new StockLineResult(item.Id, item.ItemGroupId, original.WarehouseId, original.SourceLineId, quantity, -original.Value));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    private static decimal CurrentAverage(ItemValuation v) => v.Quantity > 0 ? v.Value / v.Quantity : v.AverageCost;

    private static void Apply(ItemValuation valuation, decimal quantity, decimal value)
    {
        valuation.Quantity += quantity;
        valuation.Value += value;
        if (valuation.Quantity > 0) valuation.AverageCost = Math.Round(valuation.Value / valuation.Quantity, 6, MidpointRounding.AwayFromZero);
    }

    private StockMovement Movement(string sourceType, Guid sourceId, string sourceNo, DateOnly date, Item item, Guid? warehouseId,
        decimal quantity, decimal value, ItemValuation after, Guid? sourceLineId, Guid? reversalOf) => new()
        {
            CompanyId = tenant.RequireCompanyId(),
            ItemId = item.Id,
            WarehouseId = warehouseId,
            MovementDate = date,
            Quantity = quantity,
            UnitCost = quantity == 0 ? 0 : Math.Round(Math.Abs(value / quantity), 6, MidpointRounding.AwayFromZero),
            Value = value,
            QuantityAfter = after.Quantity,
            ValueAfter = after.Value,
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            SourceLineId = sourceLineId,
            ReversalOfId = reversalOf
        };

    private static BusinessRuleException InsufficientStock(Item item, decimal available, decimal requested) =>
        new("inv.insufficient_stock",
            $"Not enough stock of {item.Sku} {item.NameEn}: {available:0.##} available, {requested:0.##} needed.",
            $"الكمية غير كافية من {item.Sku} {item.NameAr}: المتوفر {available:0.##} والمطلوب {requested:0.##}.");

    private async Task<Dictionary<Guid, Item>> LoadStockItemsAsync(IEnumerable<Guid> ids, CancellationToken ct, bool allowInactive = false)
    {
        var distinct = ids.Distinct().ToList();
        var companyId = tenant.RequireCompanyId();
        var items = await db.Set<Item>().AsNoTracking().Where(i => distinct.Contains(i.Id) && i.CompanyId == companyId).ToDictionaryAsync(i => i.Id, ct);
        foreach (var id in distinct)
        {
            if (!items.TryGetValue(id, out var item)) throw new NotFoundException("Item", id);
            if (item.ItemType != ItemType.Stock)
                throw new BusinessRuleException("inv.not_stock_item", $"{item.Sku} is a service item and has no stock.", $"{item.Sku} صنف خدمي وليس له مخزون.");
            if (!item.IsActive && !allowInactive)
                throw new BusinessRuleException("inv.item_inactive", $"{item.Sku} is inactive.", $"الصنف {item.Sku} غير نشط.");
        }
        return items;
    }

    /// <summary>Locks the valuation rows in a fixed order (no deadlocks between documents touching the same items).</summary>
    private async Task<Dictionary<Guid, ItemValuation>> LockAsync(IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().OrderBy(id => id).ToArray();
        var locked = await db.Set<ItemValuation>().FromSqlInterpolated(
                $"SELECT * FROM inv.item_valuation WHERE item_id = ANY({ids}) ORDER BY item_id FOR UPDATE")
            .ToListAsync(ct);
        foreach (var v in locked) await db.Entry(v).ReloadAsync(ct);
        var result = locked.ToDictionary(v => v.ItemId);
        foreach (var id in ids.Where(id => !result.ContainsKey(id)))
            throw new InvalidOperationException($"Item {id} has no valuation row.");
        return result;
    }

    private async Task<WarehouseStock> WarehouseStockAsync(Guid itemId, Guid warehouseId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<WarehouseStock>().Select(e => e.Entity)
            .FirstOrDefault(s => s.ItemId == itemId && s.WarehouseId == warehouseId);
        if (tracked is not null) return tracked;
        var stock = await db.Set<WarehouseStock>().SingleOrDefaultAsync(s => s.ItemId == itemId && s.WarehouseId == warehouseId, ct);
        if (stock is not null) return stock;
        if (!await db.Set<Warehouse>().AnyAsync(w => w.Id == warehouseId && w.CompanyId == tenant.RequireCompanyId() && w.IsActive, ct))
            throw new NotFoundException("Warehouse", warehouseId);
        stock = new WarehouseStock { ItemId = itemId, WarehouseId = warehouseId };
        db.Add(stock);
        return stock;
    }

    private async Task<bool> AllowNegativeAsync(CancellationToken ct) =>
        await db.Set<InventorySettings>().Where(s => s.CompanyId == tenant.RequireCompanyId()).Select(s => s.AllowNegativeStock).FirstOrDefaultAsync(ct);
}

internal sealed class InventoryQueries(ErpDbContext db, ITenantContext tenant) : IInventoryQueries
{
    public async Task<IReadOnlyDictionary<Guid, ItemInfo>> GetItemsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        var companyId = tenant.RequireCompanyId();
        return await (from i in db.Set<Item>().AsNoTracking()
                      join u in db.Set<Uom>() on i.UomId equals u.Id
                      where list.Contains(i.Id) && i.CompanyId == companyId
                      select new ItemInfo(i.Id, i.Sku, i.NameEn, i.NameAr, i.ItemType, i.ItemGroupId, u.Code, i.IsActive))
            .ToDictionaryAsync(i => i.Id, ct);
    }

    public Task<WarehouseInfo?> GetWarehouseAsync(Guid id, CancellationToken ct = default) =>
        db.Set<Warehouse>().AsNoTracking().Where(w => w.Id == id && w.CompanyId == tenant.RequireCompanyId())
            .Select(w => new WarehouseInfo(w.Id, w.Code, w.NameEn, w.NameAr, w.IsActive)).SingleOrDefaultAsync(ct);

    public Task<decimal> GetQuantityOnHandAsync(Guid itemId, CancellationToken ct = default) =>
        db.Set<ItemValuation>().Where(v => v.ItemId == itemId).Select(v => v.Quantity).SingleOrDefaultAsync(ct);
}

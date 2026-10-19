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
internal sealed class StockLedger(ErpDbContext db, ITenantContext tenant, IPermissionService permissions) : IStockService
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
            if (!item.TrackBatches && line.Batches is { Count: > 0 })
                throw new BusinessRuleException("inv.not_batch_item", $"{item.Sku} is not tracked by batch.", $"الصنف {item.Sku} لا يُتتبع بالدفعات.");
            var batches = item.TrackBatches ? await AllocateBatchesAsync(posting, line, item, movement, allowNegative, ct) : null;
            results.Add(new StockLineResult(item.Id, item.ItemGroupId, line.WarehouseId, line.SourceLineId, line.Quantity, value, batches));
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
            var mirror = Movement(original.SourceType, original.SourceId, original.SourceNo, date, item, original.WarehouseId, quantity, -original.Value,
                valuation, original.SourceLineId, original.Id);
            db.Add(mirror);
            var batches = await MirrorBatchesAsync(original, mirror, item, allowNegative, ct);
            results.Add(new StockLineResult(item.Id, item.ItemGroupId, original.WarehouseId, original.SourceLineId, quantity, -original.Value, batches));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    public const string NoBatch = "NO-BATCH";

    /// <summary>
    /// Splits a movement over batches. Receipts go to the given batches (created on first use with their expiry); issues take the
    /// given batches or, when none are given, the batches that expire first. Expired batches are skipped unless the document may
    /// issue them (write-offs, returns, quarantine moves) or the user holds <see cref="InventoryPermissions.IssueExpired"/>.
    /// </summary>
    private async Task<IReadOnlyList<BatchQuantity>> AllocateBatchesAsync(StockPosting posting, StockLine line, Item item, StockMovement movement,
        bool allowNegative, CancellationToken ct)
    {
        var quantity = Math.Abs(line.Quantity);
        var given = line.Batches?.Where(b => b.Quantity != 0).ToList() ?? [];
        if (item.TrackSerials) await CheckSerialsAsync(line, item, given, ct);
        if (given.Any(b => b.Quantity < 0 || string.IsNullOrWhiteSpace(b.BatchNo)))
            throw new BusinessRuleException("inv.batch_values", "Each batch needs a number and a positive quantity.", "تحتاج كل دفعة إلى رقم وكمية موجبة.");
        if (given.Count > 0 && given.Sum(b => b.Quantity) != quantity)
            throw new BusinessRuleException("inv.batch_total", $"The batch quantities of {item.Sku} must add up to {quantity:0.######}.",
                $"يجب أن يساوي مجموع كميات الدفعات للصنف {item.Sku} {quantity:0.######}.");

        var allocation = new List<(Batch Batch, decimal Quantity)>();
        if (line.Quantity > 0)
        {
            if (given.Count == 0) given = [new BatchQuantity(NoBatch, quantity)];
            foreach (var b in given) allocation.Add((await BatchAsync(item, b.BatchNo.Trim().ToUpperInvariant(), b.ExpiryDate, create: true, ct), b.Quantity));
        }
        else
        {
            var canIssueExpired = posting.IssueExpired || await permissions.HasAsync(InventoryPermissions.IssueExpired, ct);
            if (given.Count > 0)
            {
                foreach (var b in given)
                {
                    var batch = await BatchAsync(item, b.BatchNo.Trim().ToUpperInvariant(), null, create: false, ct);
                    if (batch.ExpiryDate < posting.Date && !canIssueExpired) throw Expired(item, batch);
                    var available = (await BatchStockAsync(batch.Id, line.WarehouseId, ct)).Quantity;
                    if (!allowNegative && available < b.Quantity) throw InsufficientStock(item, available, b.Quantity);
                    allocation.Add((batch, b.Quantity));
                }
            }
            else
            {
                var candidates = await (from s in db.Set<BatchStock>().AsNoTracking()
                                        join b in db.Set<Batch>() on s.BatchId equals b.Id
                                        where b.ItemId == item.Id && s.WarehouseId == line.WarehouseId && s.Quantity > 0
                                        select b).ToListAsync(ct);
                var remaining = quantity;
                var skippedExpired = false;
                foreach (var candidate in candidates.OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue).ThenBy(b => b.BatchNo))
                {
                    if (remaining == 0) break;
                    if (candidate.ExpiryDate < posting.Date && !canIssueExpired)
                    {
                        skippedExpired = true;
                        continue;
                    }
                    var batch = await BatchAsync(item, candidate.BatchNo, null, create: false, ct);
                    var available = (await BatchStockAsync(batch.Id, line.WarehouseId, ct)).Quantity;
                    var take = Math.Min(available, remaining);
                    if (take <= 0) continue;
                    allocation.Add((batch, take));
                    remaining -= take;
                }
                if (remaining > 0 && item.TrackSerials) throw InsufficientStock(item, quantity - remaining, quantity);
                if (remaining > 0)
                {
                    if (skippedExpired && !allowNegative)
                        throw new BusinessRuleException("inv.batch_expired",
                            $"Only expired batches of {item.Sku} are left in this warehouse; write them off instead of issuing them.",
                            $"لم يتبقَّ من الصنف {item.Sku} في هذا المستودع إلا دفعات منتهية الصلاحية؛ قم بشطبها بدلاً من صرفها.");
                    allocation.Add((await BatchAsync(item, NoBatch, null, create: true, ct), remaining));
                }
            }
        }

        var sign = Math.Sign(line.Quantity);
        foreach (var (batch, q) in allocation)
        {
            (await BatchStockAsync(batch.Id, line.WarehouseId, ct)).Quantity += sign * q;
            db.Add(new StockMovementBatch { MovementId = movement.Id, BatchId = batch.Id, Quantity = sign * q });
        }
        return allocation.Select(a => new BatchQuantity(a.Batch.BatchNo, a.Quantity, a.Batch.ExpiryDate)).ToList();
    }

    /// <summary>
    /// Serial-numbered items move in whole units, one unit per serial. A received serial must not already be in stock; an issued one must
    /// be in this warehouse. Issues without serials take the units in serial order.
    /// </summary>
    private async Task CheckSerialsAsync(StockLine line, Item item, List<BatchQuantity> given, CancellationToken ct)
    {
        var quantity = Math.Abs(line.Quantity);
        if (quantity != decimal.Truncate(quantity))
            throw new BusinessRuleException("inv.serial_whole", $"{item.Sku} is counted by serial number: use whole units.", $"الصنف {item.Sku} يُتتبع بالرقم التسلسلي: استخدم وحدات كاملة.");
        if (given.Any(b => b.Quantity != 1 || b.BatchNo.Trim().ToUpperInvariant() == NoBatch)
            || given.Select(b => b.BatchNo.Trim().ToUpperInvariant()).Distinct().Count() != given.Count)
            throw new BusinessRuleException("inv.serial_values", "Enter each serial number once.", "أدخل كل رقم تسلسلي مرة واحدة.");
        if (line.Quantity > 0)
        {
            if (given.Count != quantity)
                throw new BusinessRuleException("inv.serials_required", $"Enter {quantity:0} serial number(s) for {item.Sku}.",
                    $"أدخل {quantity:0} رقم تسلسلي للصنف {item.Sku}.");
            var numbers = given.Select(b => b.BatchNo.Trim().ToUpperInvariant()).ToList();
            var inStock = await (from s in db.Set<BatchStock>().AsNoTracking()
                                 join b in db.Set<Batch>() on s.BatchId equals b.Id
                                 where b.ItemId == item.Id && numbers.Contains(b.BatchNo) && s.Quantity > 0
                                 select b.BatchNo).FirstOrDefaultAsync(ct);
            if (inStock is not null)
                throw new BusinessRuleException("inv.serial_in_stock", $"Serial {inStock} of {item.Sku} is already in stock.", $"الرقم التسلسلي {inStock} للصنف {item.Sku} موجود في المخزون.");
        }
        else if (given.Count > 0)
        {
            // Even where negative stock is allowed, a serial can only leave the warehouse that holds it.
            var numbers = given.Select(b => b.BatchNo.Trim().ToUpperInvariant()).ToList();
            var here = await (from s in db.Set<BatchStock>().AsNoTracking()
                              join b in db.Set<Batch>() on s.BatchId equals b.Id
                              where b.ItemId == item.Id && numbers.Contains(b.BatchNo) && s.WarehouseId == line.WarehouseId && s.Quantity > 0
                              select b.BatchNo).ToListAsync(ct);
            var missing = numbers.Except(here).FirstOrDefault();
            if (missing is not null) throw InsufficientStock(item, 0, 1);
        }
    }

    private async Task<IReadOnlyList<BatchQuantity>?> MirrorBatchesAsync(StockMovement original, StockMovement mirror, Item item, bool allowNegative,
        CancellationToken ct)
    {
        if (original.WarehouseId is not { } warehouseId) return null;
        var parts = await (from p in db.Set<StockMovementBatch>().AsNoTracking()
                           join b in db.Set<Batch>() on p.BatchId equals b.Id
                           where p.MovementId == original.Id
                           select new { p.Quantity, b.BatchNo, b.Id, b.ExpiryDate }).ToListAsync(ct);
        if (parts.Count == 0) return null;
        foreach (var p in parts)
        {
            var stock = await BatchStockAsync(p.Id, warehouseId, ct);
            if (!allowNegative && stock.Quantity - p.Quantity < 0) throw InsufficientStock(item, stock.Quantity, p.Quantity);
            stock.Quantity -= p.Quantity;
            db.Add(new StockMovementBatch { MovementId = mirror.Id, BatchId = p.Id, Quantity = -p.Quantity });
        }
        return parts.Select(p => new BatchQuantity(p.BatchNo, Math.Abs(p.Quantity), p.ExpiryDate)).ToList();
    }

    private async Task<Batch> BatchAsync(Item item, string batchNo, DateOnly? expiry, bool create, CancellationToken ct)
    {
        var batch = db.ChangeTracker.Entries<Batch>().Select(e => e.Entity).FirstOrDefault(b => b.ItemId == item.Id && b.BatchNo == batchNo)
                    ?? await db.Set<Batch>().SingleOrDefaultAsync(b => b.ItemId == item.Id && b.BatchNo == batchNo, ct);
        if (batch is null)
        {
            if (!create)
                throw new BusinessRuleException("inv.batch_unknown", $"Batch {batchNo} of {item.Sku} does not exist.", $"الدفعة {batchNo} للصنف {item.Sku} غير موجودة.");
            batch = new Batch { CompanyId = tenant.RequireCompanyId(), ItemId = item.Id, BatchNo = batchNo, ExpiryDate = expiry };
            db.Add(batch);
        }
        else if (expiry is not null && batch.ExpiryDate != expiry)
        {
            if (batch.ExpiryDate is not null)
                throw new BusinessRuleException("inv.batch_expiry", $"Batch {batchNo} of {item.Sku} already has expiry {batch.ExpiryDate:dd/MM/yyyy}.",
                    $"الدفعة {batchNo} للصنف {item.Sku} لها تاريخ انتهاء {batch.ExpiryDate:dd/MM/yyyy} مسجل مسبقاً.");
            batch.ExpiryDate = expiry;
        }
        return batch;
    }

    private async Task<BatchStock> BatchStockAsync(Guid batchId, Guid warehouseId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<BatchStock>().Select(e => e.Entity).FirstOrDefault(s => s.BatchId == batchId && s.WarehouseId == warehouseId);
        if (tracked is not null) return tracked;
        var stock = await db.Set<BatchStock>().SingleOrDefaultAsync(s => s.BatchId == batchId && s.WarehouseId == warehouseId, ct);
        if (stock is not null) return stock;
        stock = new BatchStock { BatchId = batchId, WarehouseId = warehouseId };
        db.Add(stock);
        return stock;
    }

    private static BusinessRuleException Expired(Item item, Batch batch) =>
        new("inv.batch_expired", $"Batch {batch.BatchNo} of {item.Sku} expired on {batch.ExpiryDate:dd/MM/yyyy}.",
            $"انتهت صلاحية الدفعة {batch.BatchNo} للصنف {item.Sku} في {batch.ExpiryDate:dd/MM/yyyy}.");

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
                      select new ItemInfo(i.Id, i.Sku, i.NameEn, i.NameAr, i.ItemType, i.ItemGroupId, u.Code, i.IsActive, i.SalesPrice, i.TrackBatches, i.TrackSerials))
            .ToDictionaryAsync(i => i.Id, ct);
    }

    public async Task<IReadOnlyList<CatalogItem>> GetCatalogAsync(CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        return await (from i in db.Set<Item>().AsNoTracking()
                      join u in db.Set<Uom>() on i.UomId equals u.Id
                      where i.CompanyId == companyId && i.IsActive && i.SalesPrice != null
                      orderby i.NameEn
                      select new CatalogItem(i.Id, i.Sku, i.Barcode, i.NameEn, i.NameAr, u.Code, i.ItemType, i.SalesPrice!.Value)).ToListAsync(ct);
    }

    public Task<WarehouseInfo?> GetWarehouseAsync(Guid id, CancellationToken ct = default) =>
        db.Set<Warehouse>().AsNoTracking().Where(w => w.Id == id && w.CompanyId == tenant.RequireCompanyId())
            .Select(w => new WarehouseInfo(w.Id, w.Code, w.NameEn, w.NameAr, w.IsActive)).SingleOrDefaultAsync(ct);

    public Task<decimal> GetQuantityOnHandAsync(Guid itemId, CancellationToken ct = default) =>
        db.Set<ItemValuation>().Where(v => v.ItemId == itemId).Select(v => v.Quantity).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ItemUnitInfo>> GetUnitsAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default)
    {
        var list = itemIds.Distinct().ToList();
        var companyId = tenant.RequireCompanyId();
        var baseUnits = await (from i in db.Set<Item>().AsNoTracking()
                               join u in db.Set<Uom>() on i.UomId equals u.Id
                               where list.Contains(i.Id) && i.CompanyId == companyId
                               select new ItemUnitInfo(i.Id, u.Id, u.Code, 1m, true)).ToListAsync(ct);
        var alternatives = await (from x in db.Set<ItemUnit>().AsNoTracking()
                                  join u in db.Set<Uom>() on x.UomId equals u.Id
                                  where list.Contains(x.ItemId)
                                  select new ItemUnitInfo(x.ItemId, u.Id, u.Code, x.Factor, false)).ToListAsync(ct);
        return [.. baseUnits, .. alternatives];
    }
}

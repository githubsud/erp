namespace Erp.Modules.Inventory.Contracts;

/// <summary>Stock items are counted and valued; service items (freight, maintenance, consultancy) are only bought or sold.</summary>
public enum ItemType { Stock, Service }

public sealed record ItemInfo(Guid Id, string Sku, string NameEn, string NameAr, ItemType Type, Guid ItemGroupId, string UomCode, bool IsActive,
    decimal? SalesPrice = null, bool TrackBatches = false);

/// <summary>Part of a movement in one batch. <see cref="Quantity"/> is positive (base unit); expiry is given when receiving a new batch.</summary>
public sealed record BatchQuantity(string BatchNo, decimal Quantity, DateOnly? ExpiryDate = null);

/// <summary>A unit an item can be bought or sold in, and how many base units one of it holds (the base unit itself has factor 1).</summary>
public sealed record ItemUnitInfo(Guid ItemId, Guid UomId, string UomCode, decimal Factor, bool IsBase);

public sealed record WarehouseInfo(Guid Id, string Code, string NameEn, string NameAr, bool IsActive);

/// <summary>
/// One stock movement to record. <see cref="Quantity"/> is signed (+ in, − out) in the item's base unit.
/// Receipts need <see cref="UnitCost"/>; issues are valued at the weighted-average cost unless a cost or an exact
/// <see cref="Value"/> (signed) is given. For batch-tracked items <see cref="Batches"/> splits the quantity; issues without it take
/// the batches that expire first (FEFO), receipts without it go to the item's <c>NO-BATCH</c> batch.
/// </summary>
public sealed record StockLine(Guid ItemId, Guid WarehouseId, decimal Quantity, decimal? UnitCost = null, Guid? SourceLineId = null, decimal? Value = null,
    IReadOnlyList<BatchQuantity>? Batches = null);

/// <summary>
/// <see cref="IssueExpired"/>: the document may take stock from expired batches (write-offs, returns to supplier, moves to quarantine).
/// Otherwise expired batches are only issued by users with the <c>inv.expired:issue</c> permission.
/// </summary>
public sealed record StockPosting(string SourceType, Guid SourceId, string SourceNo, DateOnly Date, IReadOnlyList<StockLine> Lines, bool IssueExpired = false);

/// <summary>What a movement did. <see cref="Value"/> is signed like the quantity; the caller posts it to the ledger.</summary>
public sealed record StockLineResult(Guid ItemId, Guid ItemGroupId, Guid? WarehouseId, Guid? SourceLineId, decimal Quantity, decimal Value,
    IReadOnlyList<BatchQuantity>? Batches = null);

/// <summary>A cost added to (or removed from) goods already received: landed cost or an invoice price difference.</summary>
public sealed record RevaluationLine(Guid ItemId, decimal Amount, decimal ReceivedQuantity, Guid? SourceLineId = null);

/// <summary>The part for quantity still on hand raises the stock value; the part for quantity already sold goes to cost of sales.</summary>
public sealed record RevaluationResult(Guid ItemId, Guid ItemGroupId, Guid? SourceLineId, decimal ToInventory, decimal ToCostOfSales);

/// <summary>
/// The only way to change stock quantities and values (weighted-average cost per item per company).
/// Must run inside the caller's transaction, next to the journal entry that posts the returned values.
/// </summary>
public interface IStockService
{
    Task<IReadOnlyList<StockLineResult>> PostAsync(StockPosting posting, CancellationToken ct = default);

    Task<IReadOnlyList<RevaluationResult>> RevalueAsync(string sourceType, Guid sourceId, string sourceNo, DateOnly date,
        IReadOnlyList<RevaluationLine> lines, CancellationToken ct = default);

    /// <summary>Mirrors every movement of a source document (exact quantities and values). A source is reversed once.</summary>
    Task<IReadOnlyList<StockLineResult>> ReverseAsync(string sourceType, Guid sourceId, DateOnly date, CancellationToken ct = default);
}

/// <summary>An active, priced item as a till sees it (barcode lookups and an offline copy).</summary>
public sealed record CatalogItem(Guid Id, string Sku, string? Barcode, string NameEn, string NameAr, string UomCode, ItemType Type, decimal SalesPrice);

public interface IInventoryQueries
{
    /// <summary>Active items that have a sales price.</summary>
    Task<IReadOnlyList<CatalogItem>> GetCatalogAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, ItemInfo>> GetItemsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    Task<WarehouseInfo?> GetWarehouseAsync(Guid id, CancellationToken ct = default);

    /// <summary>Quantity on hand for the company (all warehouses).</summary>
    Task<decimal> GetQuantityOnHandAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>The base unit and the alternative units of each item.</summary>
    Task<IReadOnlyList<ItemUnitInfo>> GetUnitsAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default);
}

public static class ItemUnits
{
    /// <summary>
    /// Base units per one of <paramref name="uomId"/> for an item (1 when no unit or the base unit is given). An unknown unit is
    /// refused with the module's error code prefix.
    /// </summary>
    public static (Guid? UomId, decimal Factor) Resolve(IReadOnlyList<ItemUnitInfo> units, ItemInfo item, Guid? uomId, string errorPrefix)
    {
        if (uomId is null) return (null, 1m);
        var unit = units.FirstOrDefault(u => u.ItemId == item.Id && u.UomId == uomId)
                   ?? throw new Erp.SharedKernel.Domain.BusinessRuleException($"{errorPrefix}.unit", $"That unit is not set up for {item.Sku}.",
                       $"هذه الوحدة غير معرفة للصنف {item.Sku}.");
        return unit.IsBase ? (null, 1m) : (unit.UomId, unit.Factor);
    }
}

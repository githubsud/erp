namespace Erp.Modules.Inventory.Contracts;

/// <summary>Stock items are counted and valued; service items (freight, maintenance, consultancy) are only bought or sold.</summary>
public enum ItemType { Stock, Service }

public sealed record ItemInfo(Guid Id, string Sku, string NameEn, string NameAr, ItemType Type, Guid ItemGroupId, string UomCode, bool IsActive,
    decimal? SalesPrice = null);

public sealed record WarehouseInfo(Guid Id, string Code, string NameEn, string NameAr, bool IsActive);

/// <summary>
/// One stock movement to record. <see cref="Quantity"/> is signed (+ in, − out) in the item's base unit.
/// Receipts need <see cref="UnitCost"/>; issues are valued at the weighted-average cost unless a cost or an exact
/// <see cref="Value"/> (signed) is given.
/// </summary>
public sealed record StockLine(Guid ItemId, Guid WarehouseId, decimal Quantity, decimal? UnitCost = null, Guid? SourceLineId = null, decimal? Value = null);

public sealed record StockPosting(string SourceType, Guid SourceId, string SourceNo, DateOnly Date, IReadOnlyList<StockLine> Lines);

/// <summary>What a movement did. <see cref="Value"/> is signed like the quantity; the caller posts it to the ledger.</summary>
public sealed record StockLineResult(Guid ItemId, Guid ItemGroupId, Guid? WarehouseId, Guid? SourceLineId, decimal Quantity, decimal Value);

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

public interface IInventoryQueries
{
    Task<IReadOnlyDictionary<Guid, ItemInfo>> GetItemsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    Task<WarehouseInfo?> GetWarehouseAsync(Guid id, CancellationToken ct = default);

    /// <summary>Quantity on hand for the company (all warehouses).</summary>
    Task<decimal> GetQuantityOnHandAsync(Guid itemId, CancellationToken ct = default);
}

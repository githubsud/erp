using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.Inventory.Domain;

public sealed class InventorySettings : TenantEntity
{
    public Guid CompanyId { get; init; }

    /// <summary>Off by default: an issue that would take a warehouse below zero is refused.</summary>
    public bool AllowNegativeStock { get; set; }
}

public sealed class Uom : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
}

public sealed class ItemGroup : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Warehouse : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid? BranchId { get; set; }
    public required string Code { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Item : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Sku { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public ItemType ItemType { get; set; }
    public Guid ItemGroupId { get; set; }
    public Guid UomId { get; set; }
    public string? Barcode { get; set; }
    public decimal? ReorderLevel { get; set; }

    /// <summary>How much to buy when stock falls to the reorder level (null: bring stock back to twice the level).</summary>
    public decimal? ReorderQuantity { get; set; }
    public decimal? PurchasePrice { get; set; }
    public decimal? SalesPrice { get; set; }
    public string? HsCode { get; set; }

    /// <summary>Quantities are kept per batch with an expiry date (food, chemicals, medicines).</summary>
    public bool TrackBatches { get; set; }

    /// <summary>Each unit has its own serial number (phones, laptops, machines). Serials are kept as one-unit batches.</summary>
    public bool TrackSerials { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

public sealed class Batch : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid ItemId { get; init; }
    public required string BatchNo { get; init; }
    public DateOnly? ExpiryDate { get; set; }
}

public sealed class BatchStock : TenantEntity, INotAudited
{
    public Guid BatchId { get; init; }
    public Guid WarehouseId { get; init; }
    public decimal Quantity { get; set; }
}

/// <summary>Insert-only split of a stock movement over batches (signed like the movement).</summary>
public sealed class StockMovementBatch : TenantEntity, IImmutable, INotAudited
{
    public Guid MovementId { get; init; }
    public Guid BatchId { get; init; }
    public decimal Quantity { get; init; }
}

/// <summary>An alternative unit of an item: one of <see cref="UomId"/> holds <see cref="Factor"/> base units.</summary>
public sealed class ItemUnit : TenantEntity
{
    public Guid ItemId { get; init; }
    public Guid UomId { get; init; }
    public decimal Factor { get; set; }
    /// <summary>The price of one of this unit (a box is often cheaper than its pieces); null: the item's price times <see cref="Factor"/>.</summary>
    public decimal? SalesPrice { get; set; }
    public string? Barcode { get; set; }
}

/// <summary>Running quantity and value of an item across the company (weighted-average cost).</summary>
public sealed class ItemValuation : TenantEntity, INotAudited
{
    public Guid ItemId { get; init; }
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }

    /// <summary>Last weighted-average unit cost; kept when the quantity reaches zero so negative issues can still be valued.</summary>
    public decimal AverageCost { get; set; }
}

public sealed class WarehouseStock : TenantEntity, INotAudited
{
    public Guid ItemId { get; init; }
    public Guid WarehouseId { get; init; }
    public decimal Quantity { get; set; }
}

/// <summary>Insert-only stock ledger. Corrections are mirror movements linked by <see cref="ReversalOfId"/>.</summary>
public sealed class StockMovement : TenantEntity, IImmutable, INotAudited
{
    public Guid CompanyId { get; init; }
    public Guid ItemId { get; init; }

    /// <summary>Null for revaluations (landed cost, price differences), which change value but not quantity.</summary>
    public Guid? WarehouseId { get; init; }
    public DateOnly MovementDate { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitCost { get; init; }
    public decimal Value { get; init; }
    public decimal QuantityAfter { get; init; }
    public decimal ValueAfter { get; init; }
    public required string SourceType { get; init; }
    public Guid SourceId { get; init; }
    public required string SourceNo { get; init; }
    public Guid? SourceLineId { get; init; }
    public Guid? ReversalOfId { get; init; }
}

public enum AdjustmentReason { Opening, Damage, Expiry, Count, Other }

public enum InventoryDocumentStatus { Posted, Reversed }

public sealed class StockAdjustment : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string AdjustmentNo { get; init; }
    public Guid WarehouseId { get; init; }
    public DateOnly AdjustmentDate { get; init; }
    public AdjustmentReason Reason { get; init; }
    public InventoryDocumentStatus Status { get; set; } = InventoryDocumentStatus.Posted;
    public string? Notes { get; init; }
    public decimal TotalValue { get; set; }
    public Guid? StockCountId { get; init; }
    public Guid? JournalEntryId { get; set; }
    public List<StockAdjustmentLine> Lines { get; init; } = [];
}

public sealed class StockAdjustmentLine : TenantEntity
{
    public Guid StockAdjustmentId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }

    /// <summary>Signed: positive adds stock, negative removes it.</summary>
    public decimal Quantity { get; init; }
    public decimal? UnitCost { get; init; }
    public decimal Value { get; set; }
}

public sealed class StockTransfer : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string TransferNo { get; init; }
    public Guid FromWarehouseId { get; init; }
    public Guid ToWarehouseId { get; init; }
    public DateOnly TransferDate { get; init; }
    public InventoryDocumentStatus Status { get; set; } = InventoryDocumentStatus.Posted;
    public string? Notes { get; init; }
    public List<StockTransferLine> Lines { get; init; } = [];
}

public sealed class StockTransferLine : TenantEntity
{
    public Guid StockTransferId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public decimal Quantity { get; init; }
    public decimal Value { get; set; }
}

public enum StockCountStatus { Open, Posted, Cancelled }

/// <summary>Physical count. System quantities are frozen when the count is opened; posting adjusts by counted − frozen.</summary>
public sealed class StockCount : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string CountNo { get; init; }
    public Guid WarehouseId { get; init; }
    public DateOnly CountDate { get; init; }
    public StockCountStatus Status { get; set; } = StockCountStatus.Open;
    public string? Notes { get; init; }
    public Guid? StockAdjustmentId { get; set; }
    public List<StockCountLine> Lines { get; init; } = [];
}

public sealed class StockCountLine : TenantEntity
{
    public Guid StockCountId { get; init; }
    public Guid ItemId { get; init; }
    public decimal SystemQuantity { get; init; }
    public decimal? CountedQuantity { get; set; }
}

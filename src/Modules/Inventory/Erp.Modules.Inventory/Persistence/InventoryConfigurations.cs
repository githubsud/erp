using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Inventory.Persistence;

internal sealed class InventorySettingsConfiguration : IEntityTypeConfiguration<InventorySettings>
{
    public void Configure(EntityTypeBuilder<InventorySettings> b) => b.ToTable("settings", "inv");
}

internal sealed class UomConfiguration : IEntityTypeConfiguration<Uom>
{
    public void Configure(EntityTypeBuilder<Uom> b) => b.ToTable("uom", "inv");
}

internal sealed class ItemGroupConfiguration : IEntityTypeConfiguration<ItemGroup>
{
    public void Configure(EntityTypeBuilder<ItemGroup> b) => b.ToTable("item_group", "inv");
}

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b) => b.ToTable("warehouse", "inv");
}

internal sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ToTable("item", "inv");
        b.Property(x => x.ReorderLevel).AsQuantity();
        b.Property(x => x.PurchasePrice).AsQuantity();
        b.Property(x => x.SalesPrice).AsQuantity();
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class ItemValuationConfiguration : IEntityTypeConfiguration<ItemValuation>
{
    public void Configure(EntityTypeBuilder<ItemValuation> b)
    {
        b.ToTable("item_valuation", "inv");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.AverageCost).AsQuantity();
    }
}

internal sealed class WarehouseStockConfiguration : IEntityTypeConfiguration<WarehouseStock>
{
    public void Configure(EntityTypeBuilder<WarehouseStock> b)
    {
        b.ToTable("warehouse_stock", "inv");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("stock_movement", "inv");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitCost).AsQuantity();
        b.Property(x => x.QuantityAfter).AsQuantity();
    }
}

internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> b)
    {
        b.ToTable("stock_adjustment", "inv");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockAdjustmentId);
    }
}

internal sealed class StockAdjustmentLineConfiguration : IEntityTypeConfiguration<StockAdjustmentLine>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentLine> b)
    {
        b.ToTable("stock_adjustment_line", "inv");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitCost).AsQuantity();
    }
}

internal sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> b)
    {
        b.ToTable("stock_transfer", "inv");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockTransferId);
    }
}

internal sealed class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> b)
    {
        b.ToTable("stock_transfer_line", "inv");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> b)
    {
        b.ToTable("stock_count", "inv");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockCountId);
    }
}

internal sealed class StockCountLineConfiguration : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> b)
    {
        b.ToTable("stock_count_line", "inv");
        b.Property(x => x.SystemQuantity).AsQuantity();
        b.Property(x => x.CountedQuantity).AsQuantity();
    }
}

internal sealed class ItemUnitConfiguration : IEntityTypeConfiguration<ItemUnit>
{
    public void Configure(EntityTypeBuilder<ItemUnit> b)
    {
        b.ToTable("item_unit", "inv");
        b.Property(x => x.Factor).AsQuantity();
    }
}

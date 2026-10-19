using Erp.Infrastructure.Persistence;
using Erp.Modules.Purchasing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Purchasing.Persistence;

internal sealed class PurchaseSettingsConfiguration : IEntityTypeConfiguration<PurchaseSettings>
{
    public void Configure(EntityTypeBuilder<PurchaseSettings> b)
    {
        b.ToTable("settings", "pur");
        b.Property(x => x.PriceTolerancePercent).HasPrecision(9, 4);
        b.Property(x => x.TaxClearanceRetentionPercent).HasPrecision(9, 4);
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("supplier", "pur");
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ToTable("purchase_order", "pur");
        b.Property(x => x.Version).HasXminConcurrency();
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("purchase_order_line", "pur");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UomFactor).AsQuantity();
        b.Property(x => x.UomQuantity).AsQuantity();
        b.Property(x => x.UomPrice).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.QuantityReceived).AsQuantity();
        b.Property(x => x.QuantityInvoiced).AsQuantity();
    }
}

internal sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> b)
    {
        b.ToTable("goods_receipt", "pur");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.GoodsReceiptId);
    }
}

internal sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> b)
    {
        b.ToTable("goods_receipt_line", "pur");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitCost).AsQuantity();
        b.Property(x => x.QuantityInvoiced).AsQuantity();
    }
}

internal sealed class SupplierInvoiceConfiguration : IEntityTypeConfiguration<SupplierInvoice>
{
    public void Configure(EntityTypeBuilder<SupplierInvoice> b)
    {
        b.ToTable("supplier_invoice", "pur");
        b.Property(x => x.Version).HasXminConcurrency();
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SupplierInvoiceId);
    }
}

internal sealed class SupplierInvoiceLineConfiguration : IEntityTypeConfiguration<SupplierInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceLine> b)
    {
        b.ToTable("supplier_invoice_line", "pur");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.ReceiptUnitCost).AsQuantity();
        b.Property(x => x.WhtRate).HasPrecision(9, 4);
        b.Property(x => x.QuantityReturned).AsQuantity();
    }
}

internal sealed class LandedCostVoucherConfiguration : IEntityTypeConfiguration<LandedCostVoucher>
{
    public void Configure(EntityTypeBuilder<LandedCostVoucher> b)
    {
        b.ToTable("landed_cost_voucher", "pur");
        b.HasMany(x => x.Charges).WithOne().HasForeignKey(x => x.LandedCostVoucherId);
        b.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.LandedCostVoucherId);
    }
}

internal sealed class LandedCostChargeConfiguration : IEntityTypeConfiguration<LandedCostCharge>
{
    public void Configure(EntityTypeBuilder<LandedCostCharge> b) => b.ToTable("landed_cost_charge", "pur");
}

internal sealed class LandedCostAllocationConfiguration : IEntityTypeConfiguration<LandedCostAllocation>
{
    public void Configure(EntityTypeBuilder<LandedCostAllocation> b)
    {
        b.ToTable("landed_cost_allocation", "pur");
        b.Property(x => x.Basis).AsQuantity();
    }
}

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> b)
    {
        b.ToTable("supplier_payment", "pur");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.SupplierPaymentId);
    }
}

internal sealed class SupplierPaymentAllocationConfiguration : IEntityTypeConfiguration<SupplierPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentAllocation> b) => b.ToTable("supplier_payment_allocation", "pur");
}

internal sealed class DebitNoteConfiguration : IEntityTypeConfiguration<DebitNote>
{
    public void Configure(EntityTypeBuilder<DebitNote> b)
    {
        b.ToTable("debit_note", "pur");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DebitNoteId);
    }
}

internal sealed class DebitNoteLineConfiguration : IEntityTypeConfiguration<DebitNoteLine>
{
    public void Configure(EntityTypeBuilder<DebitNoteLine> b)
    {
        b.ToTable("debit_note_line", "pur");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
    }
}

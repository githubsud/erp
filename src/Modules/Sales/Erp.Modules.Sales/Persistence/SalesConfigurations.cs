using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Persistence;

internal sealed class SalesSettingsConfiguration : IEntityTypeConfiguration<SalesSettings>
{
    public void Configure(EntityTypeBuilder<SalesSettings> b)
    {
        b.ToTable("settings", "sal");
        b.Property(x => x.PosMaxDiscountPercent).AsQuantity();
    }
}

internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> b)
    {
        b.ToTable("price_list", "sal");
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PriceListId);
    }
}

internal sealed class PriceListItemConfiguration : IEntityTypeConfiguration<PriceListItem>
{
    public void Configure(EntityTypeBuilder<PriceListItem> b)
    {
        b.ToTable("price_list_item", "sal");
        b.Property(x => x.UnitPrice).AsQuantity();
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customer", "sal");
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class QuotationConfiguration : IEntityTypeConfiguration<Quotation>
{
    public void Configure(EntityTypeBuilder<Quotation> b)
    {
        b.ToTable("quotation", "sal");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.QuotationId);
    }
}

internal sealed class QuotationLineConfiguration : IEntityTypeConfiguration<QuotationLine>
{
    public void Configure(EntityTypeBuilder<QuotationLine> b)
    {
        b.ToTable("quotation_line", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UomFactor).AsQuantity();
        b.Property(x => x.UomQuantity).AsQuantity();
        b.Property(x => x.UomPrice).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.DiscountPercent).HasPrecision(9, 4);
    }
}

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> b)
    {
        b.ToTable("sales_order", "sal");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.Property(x => x.Version).HasXminConcurrency();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesOrderId);
    }
}

internal sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    public void Configure(EntityTypeBuilder<SalesOrderLine> b)
    {
        b.ToTable("sales_order_line", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UomFactor).AsQuantity();
        b.Property(x => x.UomQuantity).AsQuantity();
        b.Property(x => x.UomPrice).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.DiscountPercent).HasPrecision(9, 4);
        b.Property(x => x.QuantityDelivered).AsQuantity();
        b.Property(x => x.QuantityInvoiced).AsQuantity();
    }
}

internal sealed class DeliveryNoteConfiguration : IEntityTypeConfiguration<DeliveryNote>
{
    public void Configure(EntityTypeBuilder<DeliveryNote> b)
    {
        b.ToTable("delivery_note", "sal");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DeliveryNoteId);
    }
}

internal sealed class DeliveryNoteLineConfiguration : IEntityTypeConfiguration<DeliveryNoteLine>
{
    public void Configure(EntityTypeBuilder<DeliveryNoteLine> b)
    {
        b.ToTable("delivery_note_line", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitCost).AsQuantity();
        b.Property(x => x.QuantityInvoiced).AsQuantity();
    }
}

internal sealed class SalesInvoiceConfiguration : IEntityTypeConfiguration<SalesInvoice>
{
    public void Configure(EntityTypeBuilder<SalesInvoice> b)
    {
        b.ToTable("sales_invoice", "sal");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.Property(x => x.Version).HasXminConcurrency();
        b.Property(x => x.EInvoiceUuid).HasColumnName("einvoice_uuid");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesInvoiceId);
    }
}

internal sealed class SalesInvoiceLineConfiguration : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> b)
    {
        b.ToTable("sales_invoice_line", "sal");
        b.Property(x => x.UomFactor).AsQuantity();
        b.Property(x => x.UomQuantity).AsQuantity();
        b.Property(x => x.UomPrice).AsQuantity();
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.DiscountPercent).HasPrecision(9, 4);
        b.Property(x => x.UnitCost).AsQuantity();
        b.Property(x => x.QuantityReturned).AsQuantity();
    }
}

internal sealed class CreditNoteConfiguration : IEntityTypeConfiguration<CreditNote>
{
    public void Configure(EntityTypeBuilder<CreditNote> b)
    {
        b.ToTable("credit_note", "sal");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.Property(x => x.EInvoiceUuid).HasColumnName("einvoice_uuid");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CreditNoteId);
    }
}

internal sealed class CreditNoteLineConfiguration : IEntityTypeConfiguration<CreditNoteLine>
{
    public void Configure(EntityTypeBuilder<CreditNoteLine> b)
    {
        b.ToTable("credit_note_line", "sal");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.UnitPrice).AsQuantity();
        b.Property(x => x.UnitCost).AsQuantity();
    }
}

internal sealed class CustomerReceiptConfiguration : IEntityTypeConfiguration<CustomerReceipt>
{
    public void Configure(EntityTypeBuilder<CustomerReceipt> b)
    {
        b.ToTable("customer_receipt", "sal");
        b.Property(x => x.ExchangeRate).AsQuantity();
        b.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.CustomerReceiptId);
    }
}

internal sealed class CustomerReceiptAllocationConfiguration : IEntityTypeConfiguration<CustomerReceiptAllocation>
{
    public void Configure(EntityTypeBuilder<CustomerReceiptAllocation> b) => b.ToTable("customer_receipt_allocation", "sal");
}

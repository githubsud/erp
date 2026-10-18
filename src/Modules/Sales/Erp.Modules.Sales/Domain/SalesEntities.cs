using Erp.SharedKernel.Domain;

namespace Erp.Modules.Sales.Domain;

/// <summary>What happens when an order or invoice would take a customer over its credit limit.</summary>
public enum CreditControl { None, Warn, Block }

public sealed class SalesSettings : TenantEntity
{
    public Guid CompanyId { get; init; }
    public CreditControl CreditControl { get; set; } = CreditControl.Block;
    public int QuotationValidityDays { get; set; } = 30;
}

public sealed class PriceList : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsActive { get; set; } = true;
    public List<PriceListItem> Items { get; init; } = [];
}

public sealed class PriceListItem : TenantEntity
{
    public Guid PriceListId { get; init; }
    public Guid ItemId { get; init; }
    public decimal UnitPrice { get; set; }
}

public sealed class Customer : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public string Country { get; set; } = "QA";
    public string? TaxCardNo { get; set; }
    public string? CrNo { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? AddressEn { get; set; }
    public string? AddressAr { get; set; }
    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>Maximum exposure (QAR). Null means no limit; zero means cash only.</summary>
    public decimal? CreditLimit { get; set; }
    public Guid? PriceListId { get; set; }
    public Guid? SalespersonUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>Line amount = quantity × unit price less the line discount, rounded to the dirham.</summary>
public interface IPricedLine
{
    Guid ItemId { get; }
    decimal Quantity { get; }
    decimal UnitPrice { get; }
    decimal DiscountPercent { get; }
    decimal Amount { get; }
}

public enum QuotationStatus { Draft, Sent, Accepted, Rejected }

public sealed class Quotation : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string QuoteNo { get; init; }
    public Guid CustomerId { get; set; }
    public DateOnly QuoteDate { get; set; }
    public DateOnly ValidUntil { get; set; }
    public QuotationStatus Status { get; set; } = QuotationStatus.Draft;
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public Guid? SalesOrderId { get; set; }
    public List<QuotationLine> Lines { get; init; } = [];
}

public sealed class QuotationLine : TenantEntity, IPricedLine
{
    public Guid QuotationId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal Amount { get; init; }
}

public enum SalesOrderStatus { Draft, CreditHold, Confirmed, PartiallyDelivered, Delivered, Closed, Cancelled }

public sealed class SalesOrder : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string SoNo { get; init; }
    public Guid CustomerId { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDate { get; set; }
    public Guid WarehouseId { get; set; }
    public string? CustomerRef { get; set; }
    public Guid? QuotationId { get; set; }
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;
    public decimal Total { get; set; }
    public string? Notes { get; set; }

    /// <summary>Set when the order was confirmed over the credit limit under the "warn" policy.</summary>
    public string? CreditWarning { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public Guid? CreditApprovedBy { get; set; }
    public DateTimeOffset? CreditApprovedAt { get; set; }
    public uint Version { get; set; }
    public List<SalesOrderLine> Lines { get; init; } = [];
}

public sealed class SalesOrderLine : TenantEntity, IPricedLine
{
    public Guid SalesOrderId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal Amount { get; init; }
    public decimal QuantityDelivered { get; set; }
    public decimal QuantityInvoiced { get; set; }
}

public enum DocumentStatus { Posted, Reversed }

public sealed class DeliveryNote : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string DnNo { get; init; }
    public Guid SalesOrderId { get; init; }
    public Guid CustomerId { get; init; }
    public Guid WarehouseId { get; init; }
    public DateOnly DeliveryDate { get; init; }
    public string? DeliveryAddress { get; init; }
    public string? VehicleNo { get; init; }
    public string? Notes { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public decimal TotalCost { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<DeliveryNoteLine> Lines { get; init; } = [];
}

public sealed class DeliveryNoteLine : TenantEntity
{
    public Guid DeliveryNoteId { get; init; }
    public int LineNo { get; init; }
    public Guid SalesOrderLineId { get; init; }
    public Guid ItemId { get; init; }
    public decimal Quantity { get; init; }

    /// <summary>Weighted-average cost at issue.</summary>
    public decimal UnitCost { get; set; }
    public decimal Cost { get; set; }
    public decimal QuantityInvoiced { get; set; }
}

public enum InvoiceStatus { Draft, Posted, PartiallyPaid, Paid, Reversed }

/// <summary>Delivered: goods already issued by a delivery note. Direct: stock issued by the invoice itself (counter sale). Service: no stock.</summary>
public enum InvoiceLineType { Delivered, Direct, Service }

public sealed class SalesInvoice : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string InvoiceNo { get; init; }
    public Guid CustomerId { get; init; }
    public DateOnly InvoiceDate { get; init; }
    public DateOnly DueDate { get; init; }
    public Guid? SalesOrderId { get; set; }
    public Guid? WarehouseId { get; init; }
    public string? CustomerRef { get; init; }
    public decimal Total { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal CreditedAmount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public string? Notes { get; init; }
    public Guid? EInvoiceUuid { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public Guid? JournalEntryId { get; set; }
    public uint Version { get; set; }
    public List<SalesInvoiceLine> Lines { get; init; } = [];

    public decimal OpenAmount => Total - PaidAmount - CreditedAmount;
}

public sealed class SalesInvoiceLine : TenantEntity
{
    public Guid SalesInvoiceId { get; init; }
    public int LineNo { get; init; }
    public InvoiceLineType LineType { get; init; }
    public Guid? DeliveryNoteLineId { get; init; }
    public Guid? SalesOrderLineId { get; init; }
    public Guid? ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal Amount { get; init; }

    /// <summary>Goods lines: cost per unit issued (from the delivery note, or from the invoice's own stock issue).</summary>
    public decimal? UnitCost { get; set; }
    public decimal Cost { get; set; }
    public decimal QuantityReturned { get; set; }
}

public enum CreditNoteLineType { Return, Allowance }

public sealed class CreditNote : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string CreditNoteNo { get; init; }
    public Guid CustomerId { get; init; }
    public Guid SalesInvoiceId { get; init; }
    public DateOnly NoteDate { get; init; }
    public Guid? WarehouseId { get; init; }
    public required string Reason { get; init; }
    public decimal Total { get; set; }
    public decimal CostReturned { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public Guid? EInvoiceUuid { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<CreditNoteLine> Lines { get; init; } = [];
}

public sealed class CreditNoteLine : TenantEntity
{
    public Guid CreditNoteId { get; init; }
    public int LineNo { get; init; }
    public CreditNoteLineType LineType { get; init; }
    public Guid? SalesInvoiceLineId { get; init; }
    public Guid? ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal? UnitCost { get; set; }
    public decimal Cost { get; set; }
}

public enum ReceiptMethod { Cash, BankTransfer, Cheque, Card, PostDatedCheque }

/// <summary>Posted: money received. A post-dated cheque is held until deposited (cleared) or returned unpaid (bounced).</summary>
public enum ReceiptStatus { Posted, PdcHeld, PdcCleared, Bounced, Reversed }

public sealed class CustomerReceipt : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string ReceiptNo { get; init; }
    public Guid CustomerId { get; init; }
    public DateOnly ReceiptDate { get; init; }
    public ReceiptMethod Method { get; init; }
    public Guid? DepositAccountId { get; init; }
    public string? Reference { get; init; }
    public string? ChequeNo { get; init; }
    public DateOnly? ChequeDate { get; init; }
    public string? ChequeBank { get; init; }
    public decimal Amount { get; set; }
    public ReceiptStatus Status { get; set; }
    public DateOnly? ClearedDate { get; set; }
    public Guid? ClearedAccountId { get; set; }
    public string? StatusReason { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<CustomerReceiptAllocation> Allocations { get; init; } = [];
}

public sealed class CustomerReceiptAllocation : TenantEntity
{
    public Guid CustomerReceiptId { get; init; }
    public Guid SalesInvoiceId { get; init; }
    public decimal Amount { get; init; }
}

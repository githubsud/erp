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

    /// <summary>The currency the customer is quoted and invoiced in.</summary>
    public string Currency { get; set; } = "QAR";

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

    /// <summary>The unit the line was entered in (null: the base unit), base units per it, and the quantity and price as entered.</summary>
    Guid? UomId { get; }
    decimal UomFactor { get; }
    decimal UomQuantity { get; }
    decimal UomPrice { get; }
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
    public string Currency { get; set; } = "QAR";
    public decimal ExchangeRate { get; set; } = 1m;
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
    public Guid? UomId { get; init; }
    public decimal UomFactor { get; init; } = 1m;
    public decimal UomQuantity { get; init; }
    public decimal UomPrice { get; init; }
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

    /// <summary>Amounts are in <see cref="Currency"/>; the rate values open orders in QAR for the credit check.</summary>
    public string Currency { get; set; } = "QAR";
    public decimal ExchangeRate { get; set; } = 1m;
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
    public Guid? UomId { get; init; }
    public decimal UomFactor { get; init; } = 1m;
    public decimal UomQuantity { get; init; }
    public decimal UomPrice { get; init; }
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
/// <summary>RetentionRelease bills retention held on earlier progress invoices (no revenue: it clears retentions receivable).</summary>
public enum InvoiceLineType { Delivered, Direct, Service, RetentionRelease }

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
    public string Currency { get; init; } = "QAR";

    /// <summary>QAR per unit of <see cref="Currency"/>; revenue and the receivable are booked at this rate.</summary>
    public decimal ExchangeRate { get; init; } = 1m;
    public decimal Total { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal CreditedAmount { get; set; }
    public decimal TotalBase { get; set; }
    public decimal PaidBase { get; set; }
    public decimal CreditedBase { get; set; }

    /// <summary>Contract the invoice bills (progress certificates); revenue lines carry the project tag.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Part of the total the customer keeps until handover; it is booked to retentions receivable, not to the receivable.</summary>
    public decimal RetentionAmount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public string? Notes { get; init; }

    /// <summary>An unpaid invoice brought over from the previous system (credited to opening balance equity, not revenue).</summary>
    public bool IsOpening { get; init; }
    public Guid? EInvoiceUuid { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public Guid? JournalEntryId { get; set; }
    public uint Version { get; set; }
    public List<SalesInvoiceLine> Lines { get; init; } = [];

    public decimal OpenAmount => Total - RetentionAmount - PaidAmount - CreditedAmount;
    public decimal RetentionBase => SalesRules.ToBase(RetentionAmount, ExchangeRate);
    public decimal OpenBase => TotalBase - RetentionBase - PaidBase - CreditedBase;
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

    /// <summary>The unit a direct line was entered in (null: the base unit), base units per it, and the quantity and price as entered.</summary>
    public Guid? UomId { get; init; }
    public decimal UomFactor { get; init; } = 1m;
    public decimal UomQuantity { get; init; }
    public decimal UomPrice { get; init; }
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
    public string Currency { get; init; } = "QAR";
    public decimal ExchangeRate { get; init; } = 1m;

    /// <summary>The part that reduced the invoice; the rest (when the invoice was already paid) is a credit for the customer.</summary>
    public decimal AppliedAmount { get; set; }
    public decimal AppliedBase { get; set; }
    public decimal UnappliedAmount { get; set; }
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
    public string Currency { get; init; } = "QAR";
    public decimal ExchangeRate { get; init; } = 1m;
    public decimal AmountBase { get; set; }

    /// <summary>QAR gain (positive) or loss against the invoices' rates.</summary>
    public decimal FxDifference { get; set; }

    /// <summary>Received on account: not yet allocated to an invoice (applied later or refunded).</summary>
    public decimal UnappliedAmount { get; set; }
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
    public decimal BaseCleared { get; init; }
}

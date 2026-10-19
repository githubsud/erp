using Erp.SharedKernel.Domain;

namespace Erp.Modules.Purchasing.Domain;

public sealed class PurchaseSettings : TenantEntity
{
    public Guid CompanyId { get; init; }

    /// <summary>Orders at or above this total (QAR) need approval before goods can be received.</summary>
    public decimal ApprovalThreshold { get; set; } = 10_000m;

    /// <summary>Invoice unit price above the receipt cost by more than this percentage puts the invoice on hold.</summary>
    public decimal PriceTolerancePercent { get; set; } = 2m;

    /// <summary>Share of each subcontractor payment held until a tax clearance certificate is shown. ⚠️ VERIFY with a tax adviser.</summary>
    public decimal TaxClearanceRetentionPercent { get; set; } = 3m;
}

/// <summary>Withholding tax categories for payments to non-residents without a permanent establishment (Income Tax Law 24/2018).</summary>
public enum WhtCategory { None, Royalties, TechnicalFees, Interest, Commissions, Brokerage, DirectorFees, AttendanceFees, OtherServices }

public sealed class Supplier : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public string Country { get; set; } = "QA";
    public bool IsResident { get; set; } = true;
    public bool HasPermanentEstablishment { get; set; }
    public string? TaxCardNo { get; set; }
    public string? CrNo { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public string Currency { get; set; } = "QAR";
    public WhtCategory DefaultWhtCategory { get; set; } = WhtCategory.None;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }

    /// <summary>Non-residents without a PE in Qatar suffer withholding tax on service-type payments.</summary>
    public bool SubjectToWht => !IsResident && !HasPermanentEstablishment;
}

public enum PurchaseOrderStatus { Draft, PendingApproval, Approved, PartiallyReceived, Received, Closed, Cancelled }

public sealed class PurchaseOrder : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string PoNo { get; init; }
    public Guid SupplierId { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDate { get; set; }
    public Guid WarehouseId { get; set; }
    public string Currency { get; set; } = "QAR";

    /// <summary>QAR per unit of <see cref="Currency"/>; goods are received at this rate.</summary>
    public decimal ExchangeRate { get; set; } = 1m;
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public bool RequiresApproval { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Contracts with non-residents must be notified to the GTA within 30 days.</summary>
    public DateOnly? GtaNotificationDue { get; set; }
    public string? GtaNotificationRef { get; set; }

    /// <summary>Set when the order was released from a blanket agreement or awarded from a request for quotation.</summary>
    public Guid? BlanketAgreementId { get; set; }
    public Guid? RfqId { get; set; }
    public uint Version { get; set; }
    public List<PurchaseOrderLine> Lines { get; init; } = [];
}

public sealed class PurchaseOrderLine : TenantEntity
{
    public Guid PurchaseOrderId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal QuantityReceived { get; set; }
    public decimal QuantityInvoiced { get; set; }
    public WhtCategory WhtCategory { get; init; }

    /// <summary>The unit the line was entered in (null: the base unit), base units per it, and the quantity and price as entered.</summary>
    public Guid? UomId { get; init; }
    public decimal UomFactor { get; init; } = 1m;
    public decimal UomQuantity { get; init; }
    public decimal UomPrice { get; init; }
}

public enum DocumentStatus { Posted, Reversed }

public enum PdcStatus { Issued, Cleared }

public sealed class GoodsReceipt : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string GrnNo { get; init; }
    public Guid PurchaseOrderId { get; init; }
    public Guid SupplierId { get; init; }
    public Guid WarehouseId { get; init; }
    public DateOnly ReceiptDate { get; init; }
    public decimal ExchangeRate { get; init; } = 1m;
    public string? SupplierDeliveryNote { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public decimal TotalValue { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<GoodsReceiptLine> Lines { get; init; } = [];
}

public sealed class GoodsReceiptLine : TenantEntity
{
    public Guid GoodsReceiptId { get; init; }
    public int LineNo { get; init; }
    public Guid PurchaseOrderLineId { get; init; }
    public Guid ItemId { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitCost { get; init; }
    public decimal Value { get; set; }
    public decimal QuantityInvoiced { get; set; }
}

public enum InvoiceStatus { Draft, Posted, PartiallyPaid, Paid, Reversed }

public enum MatchStatus { NotApplicable, Matched, Variance, Approved }

/// <summary>RetentionRelease / TaxRelease make retention held on earlier subcontract invoices payable (no expense: they clear the retention liability).</summary>
public enum InvoiceLineType { Goods, Service, LandedCost, RetentionRelease, TaxRelease }

public sealed class SupplierInvoice : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string InvoiceNo { get; init; }
    public Guid SupplierId { get; init; }
    public required string SupplierRef { get; init; }
    public DateOnly InvoiceDate { get; init; }
    public DateOnly DueDate { get; init; }
    public Guid? PurchaseOrderId { get; init; }

    /// <summary>Project (contract) the costs are charged to; expense lines carry it in the ledger.</summary>
    public Guid? ProjectId { get; init; }
    public string Currency { get; init; } = "QAR";
    public decimal ExchangeRate { get; init; } = 1m;

    /// <summary>Amounts in the invoice currency; the *Base amounts are what the ledger carries in QAR.</summary>
    public decimal Total { get; set; }
    public decimal TotalBase { get; set; }
    public decimal WhtAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PaidBase { get; set; }
    public decimal DebitedAmount { get; set; }
    public decimal DebitedBase { get; set; }
    public decimal WhtWithheld { get; set; }

    /// <summary>Held back from a subcontractor (QAR invoices only): not payable until released.</summary>
    public decimal RetentionAmount { get; set; }
    public decimal TaxRetentionAmount { get; set; }
    public Guid? SubcontractId { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public MatchStatus MatchStatus { get; set; } = MatchStatus.NotApplicable;
    public Guid? VarianceApprovedBy { get; set; }
    public string? Notes { get; init; }

    /// <summary>An unpaid invoice brought over from the previous system (debited to opening balance equity).</summary>
    public bool IsOpening { get; init; }
    public Guid? JournalEntryId { get; set; }
    public uint Version { get; set; }
    public List<SupplierInvoiceLine> Lines { get; init; } = [];

    public decimal Held => RetentionAmount + TaxRetentionAmount;

    public decimal OpenAmount => Total - Held - PaidAmount - DebitedAmount;

    public decimal OpenBase => TotalBase - Held - PaidBase - DebitedBase;
}

public sealed class SupplierInvoiceLine : TenantEntity
{
    public Guid SupplierInvoiceId { get; init; }
    public int LineNo { get; init; }
    public InvoiceLineType LineType { get; init; }
    public Guid? GoodsReceiptLineId { get; init; }
    public Guid? PurchaseOrderLineId { get; init; }
    public Guid? ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }

    /// <summary>Goods lines: the receipt unit cost the invoice is matched against.</summary>
    public decimal? ReceiptUnitCost { get; init; }

    /// <summary>Goods lines: amount − quantity × receipt cost.</summary>
    public decimal Variance { get; init; }
    public WhtCategory WhtCategory { get; init; }
    public decimal WhtRate { get; init; }
    public decimal WhtAmount { get; init; }
    public decimal QuantityReturned { get; set; }
}

public enum AllocationMethod { Value, Quantity }

public enum ChargeType { Freight, Clearing, CustomsDuty, Insurance, Other }

public sealed class LandedCostVoucher : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string VoucherNo { get; init; }
    public DateOnly VoucherDate { get; init; }
    public AllocationMethod Method { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public decimal TotalAmount { get; set; }
    public string? Notes { get; init; }
    public Guid? JournalEntryId { get; set; }
    public List<LandedCostCharge> Charges { get; init; } = [];
    public List<LandedCostAllocation> Allocations { get; init; } = [];
}

public sealed class LandedCostCharge : TenantEntity
{
    public Guid LandedCostVoucherId { get; init; }
    public int LineNo { get; init; }
    public ChargeType ChargeType { get; init; }
    public string? Description { get; init; }
    public decimal Amount { get; init; }
    public Guid? SupplierInvoiceId { get; init; }
}

public sealed class LandedCostAllocation : TenantEntity
{
    public Guid LandedCostVoucherId { get; init; }
    public Guid GoodsReceiptLineId { get; init; }
    public Guid ItemId { get; init; }
    public decimal Basis { get; init; }
    public decimal Amount { get; init; }
    public decimal ToInventory { get; set; }
    public decimal ToCostOfSales { get; set; }
}

public sealed class SupplierPayment : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string PaymentNo { get; init; }
    public Guid SupplierId { get; init; }
    public DateOnly PaymentDate { get; init; }
    public Guid PaymentAccountId { get; init; }
    public string? Reference { get; init; }
    public string Currency { get; init; } = "QAR";
    public decimal ExchangeRate { get; init; } = 1m;

    /// <summary>QAR gain (positive) or loss (negative) against the invoices' rates.</summary>
    public decimal FxDifference { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal WhtAmount { get; set; }
    public decimal NetAmount { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public Guid? JournalEntryId { get; set; }

    /// <summary>A post-dated cheque credits PDC payable until it clears; <see cref="PaymentAccountId"/> is the bank it is drawn on.</summary>
    public string? ChequeNo { get; init; }
    public DateOnly? ChequeDate { get; init; }
    public PdcStatus? PdcStatus { get; set; }
    public DateOnly? PdcClearedDate { get; set; }
    public List<SupplierPaymentAllocation> Allocations { get; init; } = [];
}

public sealed class SupplierPaymentAllocation : TenantEntity
{
    public Guid SupplierPaymentId { get; init; }
    public Guid SupplierInvoiceId { get; init; }
    public decimal Amount { get; init; }
    public decimal WhtAmount { get; init; }

    /// <summary>The invoice's QAR balance this allocation settles.</summary>
    public decimal BaseCleared { get; init; }
}

public enum DebitNoteLineType { Return, Allowance }

/// <summary>Goods returned to a supplier or a price allowance received, reducing a posted supplier invoice.</summary>
public sealed class DebitNote : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string DebitNoteNo { get; init; }
    public Guid SupplierId { get; init; }
    public Guid SupplierInvoiceId { get; init; }
    public DateOnly NoteDate { get; init; }
    public Guid? WarehouseId { get; init; }
    public required string Reason { get; init; }
    public decimal Total { get; set; }
    public decimal TotalBase { get; set; }

    /// <summary>The part that reduced the invoice; the rest (when the invoice was already paid) is a credit from the supplier.</summary>
    public decimal AppliedAmount { get; set; }
    public decimal AppliedBase { get; set; }
    public decimal UnappliedAmount { get; set; }

    /// <summary>Withholding tax no longer due on the invoice because of this note.</summary>
    public decimal WhtReduction { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public Guid? JournalEntryId { get; set; }
    public List<DebitNoteLine> Lines { get; init; } = [];
}

public sealed class DebitNoteLine : TenantEntity
{
    public Guid DebitNoteId { get; init; }
    public int LineNo { get; init; }
    public DebitNoteLineType LineType { get; init; }
    public Guid? SupplierInvoiceLineId { get; init; }
    public Guid? ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }

    /// <summary>Returns: the weighted-average value of the stock sent back (positive).</summary>
    public decimal StockValue { get; set; }
}

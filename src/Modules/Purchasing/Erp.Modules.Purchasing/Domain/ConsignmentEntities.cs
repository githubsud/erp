using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.Purchasing.Domain;

/// <summary>
/// A partner shop whose goods we sell as its agent (goods on trust, بضاعة أمانة). The shop is a supplier; the agreement holds the commission
/// agreed in advance, copied onto each of the shop's items, and the day of the month the shop is paid.
/// </summary>
public sealed class ConsignmentAgreement : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid SupplierId { get; init; }
    public CommissionType CommissionType { get; set; }
    public decimal Commission { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public int PayoutDay { get; set; } = 5;
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

public enum ConsignmentMovementType { Receipt, Return }

/// <summary>Bottles received from a partner shop on trust, or handed back to it. Held at zero cost: they stay the shop's property.</summary>
public sealed class ConsignmentMovement : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string MovementNo { get; init; }
    public ConsignmentMovementType MovementType { get; init; }
    public Guid SupplierId { get; init; }
    public Guid WarehouseId { get; init; }
    public DateOnly MovementDate { get; init; }
    public string? Notes { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Posted;
    public List<ConsignmentMovementLine> Lines { get; init; } = [];
}

public sealed class ConsignmentMovementLine : TenantEntity
{
    public Guid ConsignmentMovementId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public decimal Quantity { get; init; }
}

/// <summary>
/// A partner shop's monthly statement: everything owed to it up to the period end becomes one supplier invoice, paid like any other.
/// Reversing that invoice gives the amount back to the next settlement.
/// </summary>
public sealed class ConsignmentSettlement : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string SettlementNo { get; init; }
    public Guid SupplierId { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public decimal Sales { get; init; }
    public decimal Commission { get; init; }

    /// <summary>What the shop is paid: everything owed to it up to the period end (QAR).</summary>
    public decimal Amount { get; init; }
    public Guid SupplierInvoiceId { get; init; }
}

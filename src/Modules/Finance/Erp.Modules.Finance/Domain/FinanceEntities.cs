using Erp.Modules.Finance.Contracts;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.Finance.Domain;

public enum AccountType { Asset, Liability, Equity, Income, Expense }

public enum NormalBalance { Debit, Credit }

public enum ControlSubledger { Customer, Supplier, Employee }

public enum FiscalStatus { Open, Closed }

public enum VoucherStatus { Posted, Reversed }

public sealed class Account : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid? ParentId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public AccountType AccountType { get; init; }
    public NormalBalance NormalBalance { get; init; }

    /// <summary>Control accounts (receivables, payables, employee advances) only accept postings that carry a party.</summary>
    public bool IsControl { get; init; }
    public ControlSubledger? ControlSubledger { get; init; }

    /// <summary>Free classification used by screens and reports: bank, cash, retained_earnings, ...</summary>
    public string? Subtype { get; set; }

    /// <summary>Group (header) accounts are not postable.</summary>
    public bool IsPostable { get; init; } = true;
    public bool IsActive { get; set; } = true;
    public uint Version { get; private set; }

    public bool IsProfitAndLoss => AccountType is AccountType.Income or AccountType.Expense;
}

public sealed class FiscalYear : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Name { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public FiscalStatus Status { get; set; } = FiscalStatus.Open;
    public List<FiscalPeriod> Periods { get; init; } = [];
}

public sealed class FiscalPeriod : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid FiscalYearId { get; init; }
    public int PeriodNo { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public FiscalStatus Status { get; set; } = FiscalStatus.Open;
    public uint Version { get; private set; }
}

public sealed class CostCenter : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Maps an account role (optionally refined by a key such as a pay component) to an account.</summary>
public sealed class PostingRule : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string AccountRole { get; init; }
    public Guid? RoleKey { get; init; }
    public Guid AccountId { get; set; }
}

public sealed class JournalEntry : TenantEntity, IImmutable
{
    public Guid CompanyId { get; init; }
    public Guid? BranchId { get; init; }
    public required string EntryNo { get; init; }
    public DateOnly PostingDate { get; init; }
    public Guid FiscalPeriodId { get; init; }
    public required string SourceType { get; init; }
    public Guid SourceId { get; init; }
    public required string SourceNo { get; init; }
    public Guid? ReversalOfId { get; init; }
    public string? MemoEn { get; init; }
    public string? MemoAr { get; init; }
    public Guid PostedBy { get; init; }
    public DateTimeOffset PostedAt { get; init; }
    public List<JournalLine> Lines { get; init; } = [];
}

public sealed class JournalLine : TenantEntity, IImmutable
{
    public Guid JournalEntryId { get; init; }
    public int LineNo { get; init; }
    public Guid AccountId { get; init; }
    public PartyType? PartyType { get; init; }
    public Guid? PartyId { get; init; }
    public Guid? CostCenterId { get; init; }
    public Guid? ProjectId { get; init; }
    public required string Currency { get; init; }
    public decimal FxRate { get; init; }
    public decimal DebitTxn { get; init; }
    public decimal CreditTxn { get; init; }
    public decimal DebitBase { get; init; }
    public decimal CreditBase { get; init; }
    public string? Description { get; init; }
}

public enum BankStatementStatus { Open, Reconciled }

public enum BankLineStatus { Unmatched, Matched, Ignored }

/// <summary>A bank statement imported for one bank account, reconciled line by line against the ledger.</summary>
public sealed class BankStatement : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid AccountId { get; init; }
    public required string StatementNo { get; init; }
    public DateOnly DateFrom { get; init; }
    public DateOnly DateTo { get; init; }
    public decimal? OpeningBalance { get; init; }
    public decimal? ClosingBalance { get; init; }
    public string? FileName { get; init; }
    public BankStatementStatus Status { get; set; } = BankStatementStatus.Open;
    public List<BankStatementLine> Lines { get; init; } = [];
}

public sealed class BankStatementLine : TenantEntity
{
    public Guid BankStatementId { get; init; }
    public int LineNo { get; init; }
    public DateOnly TxnDate { get; init; }
    public string? Description { get; init; }
    public string? Reference { get; init; }

    /// <summary>Signed from the company's point of view: money in is positive, money out negative.</summary>
    public decimal Amount { get; init; }
    public BankLineStatus Status { get; set; } = BankLineStatus.Unmatched;

    /// <summary>Set when the ledger entry was created from this line (bank charges, interest, unrecorded receipts).</summary>
    public Guid? JournalEntryId { get; set; }
}

/// <summary>Clears one ledger line on the bank account against a statement line.</summary>
public sealed class BankMatch : TenantEntity
{
    public Guid BankStatementLineId { get; init; }
    public Guid JournalLineId { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>Manual journal voucher document. Its lines live in the journal entry it posts.</summary>
public sealed class JournalVoucher : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid? BranchId { get; init; }
    public required string VoucherNo { get; init; }
    public DateOnly VoucherDate { get; init; }
    public string? MemoEn { get; init; }
    public string? MemoAr { get; init; }
    public VoucherStatus Status { get; set; } = VoucherStatus.Posted;
    public Guid JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
}

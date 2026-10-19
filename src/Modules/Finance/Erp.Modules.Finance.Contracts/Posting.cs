namespace Erp.Modules.Finance.Contracts;

public enum PostingSide { Debit, Credit }

public enum PartyType { Customer, Supplier, Employee }

/// <summary>
/// One business fact to post. Modules describe <em>what</em> happened (a role such as "salaries payable" and
/// an amount); the posting service decides <em>which account</em> through the tenant's posting rules.
/// Either <see cref="AccountRole"/> or <see cref="AccountId"/> must be given.
/// </summary>
public sealed record PostingLine
{
    public string? AccountRole { get; init; }

    /// <summary>Optional rule key refining the role (e.g. a pay component id) before falling back to the role default.</summary>
    public Guid? RoleKey { get; init; }

    /// <summary>Explicit account (manual vouchers, user-selected bank account).</summary>
    public Guid? AccountId { get; init; }

    public PostingSide Side { get; init; }

    /// <summary>Positive amount in the request currency.</summary>
    public decimal Amount { get; init; }

    public PartyType? PartyType { get; init; }
    public Guid? PartyId { get; init; }
    public Guid? CostCenterId { get; init; }
    public Guid? ProjectId { get; init; }
    public string? Description { get; init; }
}

public sealed record PostingRequest
{
    /// <summary>Source document type, e.g. <c>payroll_run</c>, <c>journal_voucher</c>.</summary>
    public required string SourceType { get; init; }
    public required Guid SourceId { get; init; }
    public required string SourceNo { get; init; }
    public required DateOnly PostingDate { get; init; }
    public Guid? BranchId { get; init; }
    public string Currency { get; init; } = "QAR";
    public decimal FxRate { get; init; } = 1m;
    public string? MemoEn { get; init; }
    public string? MemoAr { get; init; }
    public required IReadOnlyList<PostingLine> Lines { get; init; }

    /// <summary>System postings may hit control accounts (with a party); manual vouchers may not.</summary>
    public bool AllowControlAccounts { get; init; } = true;
}

public sealed record PostingResult(Guid JournalEntryId, string EntryNo, DateOnly PostingDate, decimal TotalDebitBase);

/// <summary>
/// The only way to write the general ledger. Must be called inside the caller's database transaction, so the
/// source document, journal entry and outbox messages commit together or not at all.
/// </summary>
public interface IPostingService
{
    /// <summary>Posts once per source document. Posting the same source again returns the existing result.</summary>
    Task<PostingResult> PostAsync(PostingRequest request, CancellationToken ct = default);

    /// <summary>Creates a mirror entry linked to the original. A source can be reversed only once.</summary>
    Task<PostingResult> ReverseAsync(string sourceType, Guid sourceId, DateOnly reversalDate, string reason, CancellationToken ct = default);
}

/// <summary>Lets modules register default account mappings for their roles (e.g. per pay component) at provisioning.</summary>
public interface IPostingRuleRegistry
{
    Task EnsureRuleAsync(string accountRole, Guid? roleKey, string accountCode, CancellationToken ct = default);
}

/// <summary>Account roles understood by the posting rules. Each maps to an account per tenant.</summary>
public static class AccountRoles
{
    public const string Bank = "fin.bank";
    public const string Cash = "fin.cash";
    public const string Rounding = "fin.rounding";
    public const string OpeningBalanceEquity = "fin.opening_equity";
    public const string ExchangeDifference = "fin.fx_difference";

    public const string PayrollExpense = "hr.payroll_expense";
    public const string PayrollDeduction = "hr.payroll_deduction";
    public const string SalariesPayable = "hr.salaries_payable";
    public const string EmployeeAdvances = "hr.employee_advances";
    public const string GratuityExpense = "hr.gratuity_expense";
    public const string GratuityProvision = "hr.gratuity_provision";
    public const string LeaveSalaryExpense = "hr.leave_expense";
    public const string LeaveSalaryProvision = "hr.leave_provision";
    public const string AirTicketExpense = "hr.air_ticket_expense";
    public const string AirTicketProvision = "hr.air_ticket_provision";
    public const string SocialInsuranceExpense = "hr.social_insurance_expense";
    public const string SocialInsurancePayable = "hr.social_insurance_payable";

    /// <summary>Stock value; keyed by item group.</summary>
    public const string Inventory = "inv.inventory";
    /// <summary>Cost of goods sold; keyed by item group.</summary>
    public const string CostOfSales = "inv.cogs";
    public const string StockAdjustment = "inv.adjustment";

    public const string GoodsReceivedNotInvoiced = "pur.grni";
    public const string AccountsPayable = "pur.payable";
    public const string WithholdingTaxPayable = "pur.wht_payable";
    /// <summary>Post-dated cheques issued to suppliers and not yet presented.</summary>
    public const string PdcPayable = "pur.pdc_payable";

    /// <summary>Retention held back from subcontractors until handover.</summary>
    public const string RetentionPayable = "pur.retention_payable";

    /// <summary>Held from subcontractors until they show a tax clearance certificate.</summary>
    public const string TaxRetentionPayable = "pur.tax_retention_payable";
    /// <summary>Freight, clearing and customs invoices waiting to be allocated to receipts by a landed cost voucher.</summary>
    public const string LandedCostClearing = "pur.landed_cost_clearing";
    /// <summary>Non-stock purchases (services, supplies); keyed by item group.</summary>
    public const string PurchaseExpense = "pur.expense";

    public const string AccountsReceivable = "sal.receivable";
    /// <summary>Revenue from goods; keyed by item group.</summary>
    public const string SalesRevenue = "sal.revenue";
    /// <summary>Revenue from services; keyed by item group.</summary>
    public const string ServiceRevenue = "sal.service_revenue";
    /// <summary>Returns and allowances granted by credit notes (contra revenue).</summary>
    public const string SalesReturns = "sal.returns";
    /// <summary>Post-dated cheques received and held until their date.</summary>
    public const string PdcReceivable = "sal.pdc_receivable";
    /// <summary>Retentions held back by customers on progress invoices.</summary>
    public const string RetentionReceivable = "sal.retention_receivable";

    /// <summary>Cash counted over or short at the close of a till shift.</summary>
    public const string CashOverShort = "sal.cash_over_short";
}

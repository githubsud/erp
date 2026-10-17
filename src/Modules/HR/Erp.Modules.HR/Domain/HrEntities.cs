using Erp.SharedKernel.Domain;

namespace Erp.Modules.HR.Domain;

public enum Gender { Male, Female }

public enum EmployeeStatus { Active, Terminated }

/// <summary>Qatari nationals are covered by GRSIA (social insurance) instead of end-of-service gratuity.</summary>
public enum SocialInsuranceScheme { None, Grsia, GccHome }

public enum ComponentKind { Earning, Deduction }

/// <summary>Where a component is reported in the WPS Salary Information File.</summary>
public enum WpsField { Basic, Housing, Food, Transport, Overtime, ExtraIncome, Deduction }

public enum ContractType { Limited, Unlimited }

public enum ContractStatus { Active, Ended }

public enum GratuityBasis { Basic, BasicPlusFixedAllowances }

public enum LeaveStatus { Pending, Approved, Rejected, Cancelled }

/// <summary>How a leave type affects pay.</summary>
public enum LeavePayRule { Full, Unpaid, SickBands }

public enum OvertimeType { Normal, Night, RestDay, Holiday }

public enum PayrollStatus { Draft, Calculated, Approved, Posted, Paid, Reversed }

public enum AccrualStatus { Posted, Reversed }

public enum WpsFileStatus { Generated, Submitted, Accepted, Rejected }

public enum SettlementReason { Resignation, Termination, EndOfContract, Retirement, Death }

public enum SettlementStatus { Draft, Posted, Paid, Reversed }

public enum EmployeeDocumentType { Qid, Passport, ResidencePermit, WorkPermit, HealthCard, DrivingLicence, ProfessionalLicence, Other }

public enum CompanyDocumentType
{
    CommercialRegistration, CommercialLicense, MunicipalityLicense, EstablishmentCard, ChamberCertificate,
    TaxCard, CivilDefence, Lease, Insurance, VehicleRegistration, Other
}

/// <summary>Company payroll policy. Every legal rate is a setting so a change in law is a data change.</summary>
public sealed class HrSettings : TenantEntity
{
    public Guid CompanyId { get; init; }

    /// <summary>Establishment (computer card) ID reported as Employer EID in the SIF.</summary>
    public string? EmployerEid { get; set; }
    public string? PayerEid { get; set; }
    public string? PayerQid { get; set; }
    public string? PayerBankShortName { get; set; }
    public string? PayerIban { get; set; }
    public string SifFormatVersion { get; set; } = "1";

    public decimal GratuityDaysPerYear { get; set; } = 21;
    public decimal GratuityDivisor { get; set; } = 30;
    public int GratuityMinServiceMonths { get; set; } = 12;
    public decimal DailyRateDivisor { get; set; } = 30;
    public decimal HoursPerDay { get; set; } = 8;
    public decimal OvertimeNormalRate { get; set; } = 1.25m;
    public decimal OvertimeNightRate { get; set; } = 1.5m;
    public decimal OvertimeRestDayRate { get; set; } = 1.5m;
    public decimal OvertimeHolidayRate { get; set; } = 1.5m;
    public decimal DeductionCapPercent { get; set; } = 50;
    public decimal MinimumBasicWage { get; set; } = 1000;
    public decimal MinimumHousingAllowance { get; set; } = 500;
    public decimal MinimumFoodAllowance { get; set; } = 300;
    public int WpsDepositDays { get; set; } = 7;

    /// <summary>GRSIA contribution rates for Qatari employees (Law No. 1 of 2022). ⚠️ Verify with GRSIA.</summary>
    public decimal SiEmployeeRatePercent { get; set; } = 7;
    public decimal SiEmployerRatePercent { get; set; } = 14;
    public decimal SiMaxContributionSalary { get; set; } = 100000;
    public int SickFullPayDays { get; set; } = 14;
    public int SickHalfPayDays { get; set; } = 28;
    public int SickMinServiceMonths { get; set; } = 3;

    /// <summary>Wage used for leave salary / encashment (contract practice varies; basic by default).</summary>
    public GratuityBasis LeaveSalaryBasis { get; set; } = GratuityBasis.Basic;
}

public sealed class Department : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class PayComponent : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public ComponentKind Kind { get; init; }
    public WpsField WpsField { get; init; }

    /// <summary>Fixed components come from the contract every month; variable ones from payroll inputs.</summary>
    public bool IsFixed { get; init; }
    public bool ProrateByDays { get; init; } = true;
    public bool InGratuityBasis { get; set; }
    public bool InSocialInsuranceBasis { get; set; }

    /// <summary>Finance posting role used for this component (refined by the component id in posting rules).</summary>
    public required string PostingRole { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; set; } = true;
}

public sealed class Employee : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string EmpNo { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }

    /// <summary>ISO 3166-1 alpha-2 (QA, IN, PH, NP, EG, ...).</summary>
    public required string Nationality { get; set; }
    public Gender Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public DateOnly JoiningDate { get; set; }
    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;
    public DateOnly? TerminationDate { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CostCenterId { get; set; }
    public string? JobTitleEn { get; set; }
    public string? JobTitleAr { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }

    [Sensitive] public string? Qid { get; set; }
    [Sensitive] public string? QidHash { get; set; }
    public string? VisaId { get; set; }
    [Sensitive] public string? PassportNo { get; set; }
    public string? BankShortName { get; set; }
    [Sensitive] public string? Iban { get; set; }

    public bool InWps { get; set; } = true;
    public SocialInsuranceScheme SocialInsuranceScheme { get; set; }

    /// <summary>Gratuity liability already recognised before go-live (from the legacy system).</summary>
    public decimal OpeningGratuityLiability { get; set; }
    public uint Version { get; private set; }
}

public sealed class EmployeeDocument : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public EmployeeDocumentType DocType { get; set; }
    public string? DocNo { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? Notes { get; set; }
}

public sealed class CompanyDocument : TenantEntity
{
    public Guid CompanyId { get; init; }
    public CompanyDocumentType DocType { get; set; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public string? DocNo { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public Guid? ResponsibleUserId { get; set; }
    public string? Notes { get; set; }
}

public sealed class EmploymentContract : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; set; }
    public ContractType ContractType { get; init; }
    public int ProbationMonths { get; init; }
    public bool AccommodationProvided { get; init; }
    public bool FoodProvided { get; init; }

    /// <summary>Overrides the company default (21 days) when the contract is more generous.</summary>
    public decimal? GratuityDaysPerYear { get; init; }
    public GratuityBasis GratuityBasis { get; init; }
    public int? AnnualLeaveDays { get; init; }
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    public List<ContractComponent> Components { get; init; } = [];
}

public sealed class ContractComponent : TenantEntity
{
    public Guid ContractId { get; init; }
    public Guid PayComponentId { get; init; }
    public decimal Amount { get; init; }
}

public sealed class LeaveType : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public bool IsPaid { get; init; }
    public LeavePayRule PayRule { get; init; } = LeavePayRule.Full;

    /// <summary>Unpaid leave is excluded from service when calculating gratuity.</summary>
    public bool AffectsService { get; init; }
    public bool DeductsAnnualBalance { get; init; }
    public bool IsSystem { get; init; }
}

public sealed class LeaveRequest : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public Guid LeaveTypeId { get; init; }
    public DateOnly FromDate { get; init; }
    public DateOnly ToDate { get; init; }
    public decimal Days { get; init; }
    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;
    public string? Reason { get; init; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Variable pay for a month: overtime hours or an amount (bonus, deduction, advance recovery).</summary>
public sealed class PayrollInput : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public Guid PayComponentId { get; init; }
    public OvertimeType? OvertimeType { get; init; }
    public decimal? Hours { get; init; }
    public decimal? Amount { get; init; }
    public string? Notes { get; init; }
}

public sealed class PayrollRun : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string RunNo { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }

    /// <summary>Wages for the month fall due on the 1st of the following month (Ministerial Decision 50/2026).</summary>
    public DateOnly DueDate { get; init; }

    /// <summary>Last day to deposit wages through WPS (due date + 7 days).</summary>
    public DateOnly WpsDeadline { get; init; }
    public PayrollStatus Status { get; set; } = PayrollStatus.Draft;
    public int EmployeeCount { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public decimal TotalGratuityAccrual { get; set; }
    public Guid? CalculatedBy { get; set; }
    public DateTimeOffset? CalculatedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateOnly? PaymentDate { get; set; }
    public Guid? PaymentAccountId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? GratuityJournalEntryId { get; set; }
    public Guid? PaymentJournalEntryId { get; set; }
    public uint Version { get; private set; }
}

public sealed class Payslip : TenantEntity
{
    public Guid PayrollRunId { get; init; }
    public Guid EmployeeId { get; init; }
    public required string EmpNo { get; init; }
    public required string NameEn { get; init; }
    public required string NameAr { get; init; }
    public Guid? CostCenterId { get; init; }
    [Sensitive] public string? Qid { get; init; }
    public string? VisaId { get; init; }
    public string? BankShortName { get; init; }
    [Sensitive] public string? Iban { get; init; }
    public bool InWps { get; init; }
    public decimal PaidDays { get; init; }
    public decimal UnpaidDays { get; init; }
    public decimal HalfPayDays { get; init; }
    public decimal EmployerSocialInsurance { get; init; }
    public decimal BasicSalary { get; init; }
    public decimal OvertimeHours { get; init; }
    public decimal Gross { get; init; }
    public decimal Deductions { get; init; }
    public decimal Net { get; init; }
    public string? Warnings { get; init; }
    public List<PayslipLine> Lines { get; init; } = [];
}

public sealed class PayslipLine : TenantEntity
{
    public Guid PayslipId { get; init; }
    public int LineNo { get; init; }
    public Guid PayComponentId { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; init; }
    public required string NameAr { get; init; }
    public ComponentKind Kind { get; init; }
    public WpsField WpsField { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Rate { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>Monthly end-of-service liability per employee; the delta is what gets expensed.</summary>
public sealed class GratuityAccrual : TenantEntity
{
    public Guid EmployeeId { get; init; }
    public Guid PayrollRunId { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public decimal BasisWage { get; init; }
    public decimal DaysPerYear { get; init; }
    public int ServiceDays { get; init; }
    public decimal Liability { get; init; }
    public decimal PreviousLiability { get; init; }
    public decimal Delta { get; init; }
    public AccrualStatus Status { get; set; } = AccrualStatus.Posted;
}

public sealed class WpsFile : TenantEntity
{
    public Guid PayrollRunId { get; init; }
    public required string FormatVersion { get; init; }
    public required string PayerBankShortName { get; init; }
    public required string FileName { get; init; }
    public required string Content { get; init; }
    public required string Sha256 { get; init; }
    public decimal TotalSalaries { get; init; }
    public int RecordCount { get; init; }
    public WpsFileStatus Status { get; set; } = WpsFileStatus.Generated;
}

/// <summary>End-of-service settlement: gratuity payout, leave encashment, unpaid salary and recoveries, posted once.</summary>
public sealed class FinalSettlement : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid EmployeeId { get; init; }
    public required string SettlementNo { get; init; }
    public DateOnly LastWorkingDay { get; init; }
    public SettlementReason Reason { get; init; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Draft;
    public DateOnly JoiningDate { get; init; }
    public int ServiceDays { get; init; }
    public int ExcludedDays { get; init; }
    public decimal BasisWage { get; init; }
    public decimal GratuityDaysPerYear { get; init; }
    public bool EligibleForGratuity { get; init; }
    public decimal GratuityAmount { get; init; }

    /// <summary>Gratuity already provided for in the books (released when the settlement is posted).</summary>
    public decimal GratuityProvision { get; init; }
    public decimal LeaveBalanceDays { get; init; }
    public decimal DailyLeaveRate { get; init; }
    public DateOnly? UnpaidSalaryFrom { get; init; }
    public decimal UnpaidSalaryDays { get; init; }
    public decimal TotalEarnings { get; init; }
    public decimal TotalDeductions { get; init; }
    public decimal NetPayable { get; init; }
    public string? Notes { get; init; }
    public bool EmployeeWasActive { get; init; }
    public Guid? EndedContractId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateOnly? PaymentDate { get; set; }
    public Guid? PaymentAccountId { get; set; }
    public Guid? PaymentJournalEntryId { get; set; }
    public List<FinalSettlementLine> Lines { get; init; } = [];
}

public sealed class FinalSettlementLine : TenantEntity
{
    public Guid FinalSettlementId { get; init; }
    public int LineNo { get; init; }
    public required string Code { get; init; }
    public required string NameEn { get; init; }
    public required string NameAr { get; init; }
    public ComponentKind Kind { get; init; }
    public Guid? PayComponentId { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Rate { get; init; }
    public decimal Amount { get; init; }
}

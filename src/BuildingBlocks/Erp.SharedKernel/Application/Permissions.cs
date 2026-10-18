namespace Erp.SharedKernel.Application;

/// <summary>Default role codes created for every tenant. Tenants can add or edit roles later.</summary>
public static class DefaultRoles
{
    public const string Owner = "owner";
    public const string GeneralManager = "general_manager";
    public const string Accountant = "accountant";
    public const string HrManager = "hr_manager";
    public const string PayrollOfficer = "payroll_officer";
    public const string Auditor = "auditor";
    public const string Purchaser = "purchaser";
    public const string Storekeeper = "storekeeper";
    public const string Salesperson = "salesperson";

    /// <summary>Employees using self-service: their own payslips, leave and documents only.</summary>
    public const string Employee = "employee";

    /// <summary>Grants every permission.</summary>
    public const string Wildcard = "*";
}

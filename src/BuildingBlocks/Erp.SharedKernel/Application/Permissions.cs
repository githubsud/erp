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

    /// <summary>Grants every permission.</summary>
    public const string Wildcard = "*";
}

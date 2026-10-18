using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Finance;

public static class FinancePermissions
{
    public const string AccountsRead = "fin.accounts:read";
    public const string AccountsManage = "fin.accounts:manage";
    public const string PeriodsManage = "fin.periods:manage";
    public const string RulesManage = "fin.rules:manage";
    public const string JournalRead = "fin.journal:read";
    public const string JournalPost = "fin.journal:post";
    public const string JournalReverse = "fin.journal:reverse";
    public const string ReportsRead = "fin.reports:read";
    public const string BankReconcile = "fin.bank:reconcile";
}

internal sealed class FinancePermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(FinancePermissions.AccountsRead, "View chart of accounts", "عرض دليل الحسابات", Accountant, GeneralManager, Auditor, PayrollOfficer),
        new(FinancePermissions.AccountsManage, "Manage chart of accounts and cost centres", "إدارة دليل الحسابات ومراكز التكلفة", Accountant),
        new(FinancePermissions.PeriodsManage, "Open and close fiscal periods", "فتح وإغلاق الفترات المالية", Accountant),
        new(FinancePermissions.RulesManage, "Manage posting rules", "إدارة قواعد الترحيل", Accountant),
        new(FinancePermissions.JournalRead, "View journal entries", "عرض القيود", Accountant, GeneralManager, Auditor),
        new(FinancePermissions.JournalPost, "Post journal vouchers", "ترحيل قيود اليومية", Accountant),
        new(FinancePermissions.JournalReverse, "Reverse journal vouchers", "عكس قيود اليومية", Accountant),
        new(FinancePermissions.ReportsRead, "View financial reports", "عرض التقارير المالية", Accountant, GeneralManager, Auditor),
        new(FinancePermissions.BankReconcile, "Import bank statements and reconcile", "استيراد كشوف البنك والمطابقة", Accountant)
    ];
}

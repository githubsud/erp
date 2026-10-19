using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.HR;

public static class HrPermissions
{
    public const string SettingsManage = "hr.settings:manage";
    public const string EmployeesRead = "hr.employees:read";
    public const string EmployeesManage = "hr.employees:manage";

    /// <summary>Field-level: QID, passport, IBAN and salary amounts are masked without this permission.</summary>
    public const string SensitiveView = "hr.sensitive:view";
    public const string ContractsManage = "hr.contracts:manage";
    public const string DocumentsManage = "hr.documents:manage";
    public const string LeaveManage = "hr.leave:manage";
    public const string RostersManage = "hr.rosters:manage";
    public const string ExpensesApprove = "hr.expenses:approve";
    public const string LeaveApprove = "hr.leave:approve";
    public const string PayrollRead = "hr.payroll:read";
    public const string PayrollPrepare = "hr.payroll:prepare";
    public const string PayrollApprove = "hr.payroll:approve";
    public const string PayrollPost = "hr.payroll:post";

    /// <summary>Employee self-service: the signed-in employee's own payslips, leave and documents.</summary>
    public const string SelfService = "ess.self:use";
}

internal sealed class HrPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(HrPermissions.SettingsManage, "Manage HR and WPS settings", "إدارة إعدادات الموارد البشرية وحماية الأجور", HrManager),
        new(HrPermissions.EmployeesRead, "View employees", "عرض الموظفين", HrManager, PayrollOfficer, GeneralManager, Accountant, Auditor),
        new(HrPermissions.EmployeesManage, "Create and edit employees", "إضافة وتعديل الموظفين", HrManager, PayrollOfficer),
        new(HrPermissions.SensitiveView, "View QID, passport, IBAN and salaries", "عرض الرقم الشخصي والجواز والآيبان والرواتب", HrManager, PayrollOfficer, GeneralManager),
        new(HrPermissions.ContractsManage, "Manage employment contracts", "إدارة عقود العمل", HrManager),
        new(HrPermissions.DocumentsManage, "Manage employee and company documents", "إدارة مستندات الموظفين والشركة", HrManager, PayrollOfficer),
        new(HrPermissions.LeaveManage, "Record leave requests", "تسجيل طلبات الإجازة", HrManager, PayrollOfficer),
        new(HrPermissions.RostersManage, "Plan shifts and rosters", "تخطيط الورديات وجداول العمل", HrManager, PayrollOfficer),
        new(HrPermissions.ExpensesApprove, "Approve employee expense claims", "اعتماد مطالبات مصروفات الموظفين", HrManager, GeneralManager),
        new(HrPermissions.LeaveApprove, "Approve leave", "اعتماد الإجازات", HrManager, GeneralManager),
        new(HrPermissions.PayrollRead, "View payroll", "عرض الرواتب", HrManager, PayrollOfficer, GeneralManager, Accountant, Auditor),
        new(HrPermissions.PayrollPrepare, "Prepare and calculate payroll", "إعداد واحتساب الرواتب", HrManager, PayrollOfficer),
        new(HrPermissions.PayrollApprove, "Approve payroll", "اعتماد الرواتب", GeneralManager),
        new(HrPermissions.PayrollPost, "Post payroll, generate WPS files and record payment", "ترحيل الرواتب وإنشاء ملفات حماية الأجور وتسجيل الصرف", Accountant),
        new(HrPermissions.SelfService, "Use employee self-service", "استخدام الخدمة الذاتية للموظف", Employee)
    ];
}

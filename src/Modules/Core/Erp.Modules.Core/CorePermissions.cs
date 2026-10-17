using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Core;

public static class CorePermissions
{
    public const string CompanyManage = "core.company:manage";
    public const string BranchesManage = "core.branches:manage";
    public const string UsersRead = "core.users:read";
    public const string UsersManage = "core.users:manage";
    public const string RolesManage = "core.roles:manage";
    public const string AuditRead = "core.audit:read";
}

internal sealed class CorePermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(CorePermissions.CompanyManage, "Edit company profile", "تعديل بيانات الشركة", GeneralManager),
        new(CorePermissions.BranchesManage, "Manage branches", "إدارة الفروع", GeneralManager),
        new(CorePermissions.UsersRead, "View users", "عرض المستخدمين", GeneralManager, Auditor),
        new(CorePermissions.UsersManage, "Manage users", "إدارة المستخدمين", GeneralManager),
        new(CorePermissions.RolesManage, "Manage roles and permissions", "إدارة الأدوار والصلاحيات"),
        new(CorePermissions.AuditRead, "View audit trail", "عرض سجل التدقيق", GeneralManager, Accountant, Auditor)
    ];
}

/// <summary>Bilingual names for the default roles created at provisioning.</summary>
internal static class DefaultRoleNames
{
    public static readonly IReadOnlyList<(string Code, string En, string Ar)> All =
    [
        (Owner, "Owner", "المالك"),
        (GeneralManager, "General Manager", "المدير العام"),
        (Accountant, "Accountant", "المحاسب"),
        (HrManager, "HR Manager", "مدير الموارد البشرية"),
        (PayrollOfficer, "Payroll Officer", "مسؤول الرواتب"),
        (Auditor, "Auditor (read-only)", "المدقق (قراءة فقط)")
    ];
}

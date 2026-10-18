using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Purchasing;

public static class PurchasingPermissions
{
    public const string SuppliersRead = "pur.suppliers:read";
    public const string SuppliersManage = "pur.suppliers:manage";
    public const string OrdersManage = "pur.orders:manage";
    public const string OrdersApprove = "pur.orders:approve";
    public const string ReceiptsPost = "pur.receipts:post";
    public const string InvoicesPost = "pur.invoices:post";
    public const string InvoicesApprove = "pur.invoices:approve";
    public const string PaymentsPost = "pur.payments:post";
    public const string ReportsRead = "pur.reports:read";
    public const string SettingsManage = "pur.settings:manage";
}

internal sealed class PurchasingPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(PurchasingPermissions.SuppliersRead, "View suppliers and purchasing documents", "عرض الموردين ومستندات المشتريات",
            Purchaser, Storekeeper, Accountant, GeneralManager, Auditor),
        new(PurchasingPermissions.SuppliersManage, "Create and edit suppliers", "إضافة وتعديل الموردين", Purchaser, Accountant),
        new(PurchasingPermissions.OrdersManage, "Create and submit purchase orders", "إنشاء وإرسال أوامر الشراء", Purchaser),
        new(PurchasingPermissions.OrdersApprove, "Approve purchase orders", "اعتماد أوامر الشراء", GeneralManager),
        new(PurchasingPermissions.ReceiptsPost, "Receive goods", "استلام البضاعة", Storekeeper, Purchaser),
        new(PurchasingPermissions.InvoicesPost, "Enter and post supplier invoices and landed costs", "إدخال وترحيل فواتير الموردين وتكاليف الاستيراد", Accountant),
        new(PurchasingPermissions.InvoicesApprove, "Approve invoice price variances", "اعتماد فروقات أسعار الفواتير", GeneralManager),
        new(PurchasingPermissions.PaymentsPost, "Pay suppliers", "سداد الموردين", Accountant),
        new(PurchasingPermissions.ReportsRead, "View payables, GRNI and withholding tax reports", "عرض تقارير الذمم الدائنة والاستقطاع", Accountant, GeneralManager, Auditor),
        new(PurchasingPermissions.SettingsManage, "Manage purchasing settings", "إدارة إعدادات المشتريات", GeneralManager, Accountant)
    ];
}

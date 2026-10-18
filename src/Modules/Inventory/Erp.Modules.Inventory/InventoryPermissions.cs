using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Inventory;

public static class InventoryPermissions
{
    public const string ItemsRead = "inv.items:read";
    public const string ItemsManage = "inv.items:manage";
    public const string StockPost = "inv.stock:post";
    public const string ReportsRead = "inv.reports:read";
    public const string SettingsManage = "inv.settings:manage";
}

internal sealed class InventoryPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(InventoryPermissions.ItemsRead, "View items and stock", "عرض الأصناف والمخزون", Storekeeper, Purchaser, Accountant, GeneralManager, Auditor),
        new(InventoryPermissions.ItemsManage, "Create and edit items", "إضافة وتعديل الأصناف", Storekeeper, Purchaser),
        new(InventoryPermissions.StockPost, "Post stock adjustments, transfers and counts", "ترحيل تسويات المخزون والتحويلات والجرد", Storekeeper, Accountant),
        new(InventoryPermissions.ReportsRead, "View stock valuation", "عرض تقييم المخزون", Accountant, GeneralManager, Auditor, Storekeeper),
        new(InventoryPermissions.SettingsManage, "Manage warehouses and inventory settings", "إدارة المستودعات وإعدادات المخزون", Accountant, GeneralManager)
    ];
}

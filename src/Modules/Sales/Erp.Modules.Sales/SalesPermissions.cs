using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Sales;

public static class SalesPermissions
{
    public const string CustomersRead = "sal.customers:read";
    public const string CustomersManage = "sal.customers:manage";
    public const string OrdersManage = "sal.orders:manage";
    public const string CreditApprove = "sal.credit:approve";
    public const string DeliveriesPost = "sal.deliveries:post";
    public const string InvoicesPost = "sal.invoices:post";
    public const string ReceiptsPost = "sal.receipts:post";
    public const string ReportsRead = "sal.reports:read";
    public const string SettingsManage = "sal.settings:manage";
}

internal sealed class SalesPermissionProvider : IPermissionProvider
{
    // Keep in step with V0501__sales_roles.sql, which grants the same defaults to existing companies.
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(SalesPermissions.CustomersRead, "View customers and sales documents", "عرض العملاء ومستندات المبيعات",
            Salesperson, Storekeeper, Accountant, GeneralManager, Auditor),
        new(SalesPermissions.CustomersManage, "Create and edit customers and price lists", "إضافة وتعديل العملاء وقوائم الأسعار", Salesperson, Accountant),
        new(SalesPermissions.OrdersManage, "Create quotations and sales orders", "إنشاء عروض الأسعار وأوامر البيع", Salesperson),
        new(SalesPermissions.CreditApprove, "Release orders and invoices over the credit limit", "اعتماد الأوامر والفواتير المتجاوزة لحد الائتمان", GeneralManager),
        new(SalesPermissions.DeliveriesPost, "Deliver goods to customers", "تسليم البضاعة للعملاء", Storekeeper, Salesperson),
        new(SalesPermissions.InvoicesPost, "Issue sales invoices and credit notes", "إصدار فواتير المبيعات والإشعارات الدائنة", Accountant, Salesperson),
        new(SalesPermissions.ReceiptsPost, "Receive customer payments and cheques", "استلام دفعات وشيكات العملاء", Accountant),
        new(SalesPermissions.ReportsRead, "View receivables and sales reports", "عرض تقارير الذمم المدينة والمبيعات", Accountant, GeneralManager, Auditor),
        new(SalesPermissions.SettingsManage, "Manage sales settings", "إدارة إعدادات المبيعات", GeneralManager, Accountant)
    ];
}

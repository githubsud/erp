using Erp.SharedKernel.Application;
using static Erp.SharedKernel.Application.DefaultRoles;

namespace Erp.Modules.Commerce;

public static class CommercePermissions
{
    public const string StoreManage = "commerce.store:manage";
    public const string ListingEdit = "commerce.listing:edit";
    public const string OrderView = "commerce.order:view";
    public const string OrderFulfil = "commerce.order:fulfil";
    public const string RefundCreate = "commerce.refund:create";
}

internal sealed class CommercePermissionProvider : IPermissionProvider
{
    // The owner has every permission. Keep in step with V0700__commerce_foundation.sql, which grants the same defaults to existing companies.
    public IEnumerable<PermissionDefinition> GetPermissions() =>
    [
        new(CommercePermissions.StoreManage, "Set up the online store", "إعداد المتجر الإلكتروني", GeneralManager),
        new(CommercePermissions.ListingEdit, "Publish products and categories in the online store", "نشر المنتجات والفئات في المتجر الإلكتروني",
            GeneralManager, Storekeeper),
        new(CommercePermissions.OrderView, "View online orders", "عرض الطلبات الإلكترونية", Salesperson, Storekeeper),
        new(CommercePermissions.OrderFulfil, "Pick, pack and deliver online orders", "تجهيز وتسليم الطلبات الإلكترونية", Salesperson, Storekeeper),
        new(CommercePermissions.RefundCreate, "Refund online payments", "استرداد المدفوعات الإلكترونية", Accountant)
    ];
}

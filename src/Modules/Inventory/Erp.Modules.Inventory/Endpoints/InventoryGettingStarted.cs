using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Endpoints;

/// <summary>Products with prices and barcodes, and the stock already on the shelves.</summary>
internal sealed class InventoryGettingStarted(ErpDbContext db, ITenantContext tenant) : IGettingStartedProvider
{
    public string Permission => InventoryPermissions.ItemsManage;
    public string? Module => ModuleCodes.Inventory;

    public async Task<IReadOnlyList<GettingStartedStep>> GetStepsAsync(CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var items = await db.Set<Item>().AnyAsync(i => i.CompanyId == companyId, ct);
        var stock = await db.Set<StockMovement>().AnyAsync(m => m.CompanyId == companyId, ct)
                    || !await db.Set<Item>().AnyAsync(i => i.CompanyId == companyId && i.ItemType == ItemType.Stock, ct) && items;
        return
        [
            new("items", 30, "Add your products and prices", "أضف منتجاتك وأسعارها",
                "Type them in, or import your price list from Excel (with barcodes).", "أدخلها يدوياً، أو استورد قائمة أسعارك من إكسل (مع الباركود).",
                "/inventory/items", items),
            new("stock", 40, "Enter the stock on your shelves", "أدخل المخزون الموجود لديك",
                "Add opening quantities in the same Excel file, or count the shelves.", "أضف الكميات الافتتاحية في ملف الإكسل نفسه، أو قم بجرد الرفوف.",
                "/inventory/items", stock)
        ];
    }
}

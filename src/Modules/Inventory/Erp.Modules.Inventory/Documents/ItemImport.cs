using Erp.Infrastructure.Import;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Documents;

/// <summary>
/// A shop's price list from Excel: new items are created, items whose code exists are updated (blank cells keep the current
/// value), and opening quantities become one opening-stock adjustment per warehouse, valued at the given cost.
/// </summary>
internal sealed class ItemImportService(ErpDbContext db, ITenantContext tenant, InventoryMasterService masters, StockAdjustmentService adjustments,
    IPermissionService permissions, IClock clock)
{
    internal static readonly ImportColumn[] Columns =
    [
        new("Sku", "Item code", "رمز الصنف", true),
        new("NameAr", "Name (Arabic)", "الاسم بالعربية"),
        new("NameEn", "Name (English)", "الاسم بالإنجليزية"),
        new("Type", "Stock or Service", "مخزني أو خدمة", Choices: ["Stock", "Service"]),
        new("Barcode", "Barcode", "الباركود"),
        new("SalesPrice", "Selling price (QAR)", "سعر البيع (ريال)"),
        new("PurchasePrice", "Purchase price (QAR)", "سعر الشراء (ريال)"),
        new("Group", "Item group code (blank: GEN)", "رمز المجموعة (فارغ: GEN)"),
        new("Unit", "Unit code (blank: PCS)", "رمز الوحدة (فارغ: PCS)"),
        new("ReorderLevel", "Reorder level", "حد إعادة الطلب"),
        new("OpeningQty", "Opening quantity (new items)", "الكمية الافتتاحية (للأصناف الجديدة)"),
        new("OpeningCost", "Opening unit cost (blank: purchase price)", "تكلفة الوحدة الافتتاحية (فارغ: سعر الشراء)"),
        new("Warehouse", "Warehouse code (blank: main)", "رمز المستودع (فارغ: الرئيسي)")
    ];

    public static byte[] Template() => ExcelImport.Template("Items", Columns,
        [
            ["MILK-1L", "حليب طازج 1 لتر", "Fresh milk 1L", "Stock", "6281007012345", "6.5", "5", "", "", "24", "48", "", ""],
            ["RICE-5KG", "أرز بسمتي 5 كغ", "Basmati rice 5kg", "Stock", "8901234567890", "32", "25", "", "", "10", "20", "", ""],
            ["ALTER", "تعديل ملابس", "Alteration", "Service", "", "25", "", "", "", "", "", "", ""]
        ],
        [
            "One item per row. Existing item codes are updated (empty cells keep the current value); new codes are created.",
            "Opening quantity is taken in only for items without stock movements yet. It is valued at OpeningCost, or the purchase price.",
            "Group, Unit and Warehouse use the codes shown in the system (Inventory settings); leave them blank for the defaults."
        ],
        [
            "صنف واحد في كل صف. الأصناف الموجودة بنفس الرمز تُحدّث (الخلايا الفارغة تبقي القيمة الحالية)، والرموز الجديدة تُضاف.",
            "الكمية الافتتاحية تُضاف فقط للأصناف التي ليس لها حركات مخزون بعد، وتُقيّم بتكلفة الوحدة الافتتاحية أو سعر الشراء.",
            "المجموعة والوحدة والمستودع بالرموز الظاهرة في النظام (إعدادات المخزون)؛ اتركها فارغة للقيم الافتراضية."
        ]);

    public async Task<ImportSummary> ImportAsync(Stream file, bool dryRun, CancellationToken ct)
    {
        var rows = ExcelImport.Read(file, Columns);
        var companyId = tenant.RequireCompanyId();
        var groups = await db.Set<ItemGroup>().AsNoTracking().Where(g => g.CompanyId == companyId).ToDictionaryAsync(g => g.Code.ToUpperInvariant(), g => g.Id, ct);
        var units = await db.Set<Uom>().AsNoTracking().Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Code.ToUpperInvariant(), u => u.Id, ct);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().Where(w => w.CompanyId == companyId && w.IsActive)
            .OrderBy(w => w.Code).ToListAsync(ct);
        var canPostStock = await permissions.HasAsync(InventoryPermissions.StockPost, ct);
        var opening = new Dictionary<Guid, List<AdjustmentLineRequest>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Guid Lookup(Dictionary<string, Guid> map, string? code, string fallback, string what, ImportRow row)
        {
            var key = (code ?? fallback).ToUpperInvariant();
            if (map.TryGetValue(key, out var id)) return id;
            if (code is null && map.Count > 0) return map.Values.First();
            row.Errors.Add($"{what} '{key}' does not exist / غير موجود");
            return Guid.Empty;
        }

        return await ExcelImport.RunAsync(db, Columns, rows, dryRun, async (row, token) =>
        {
            var sku = row.Text("Sku")!.ToUpperInvariant();
            if (!seen.Add(sku)) row.Errors.Add($"Item code {sku} appears twice in the file / الرمز مكرر في الملف");
            var existing = await db.Set<Item>().AsNoTracking().SingleOrDefaultAsync(i => i.CompanyId == companyId && i.Sku == sku, token);
            var type = row.Text("Type") switch
            {
                null => existing?.ItemType ?? ItemType.Stock,
                var t when t.Equals("Service", StringComparison.OrdinalIgnoreCase) || t.Contains("خدم") => ItemType.Service,
                _ => ItemType.Stock
            };
            var group = row.Text("Group") is null && existing is not null ? existing.ItemGroupId : Lookup(groups, row.Text("Group"), "GEN", "Group", row);
            var unit = row.Text("Unit") is null && existing is not null ? existing.UomId : Lookup(units, row.Text("Unit"), "PCS", "Unit", row);
            var sales = row.Number("SalesPrice");
            var purchase = row.Number("PurchasePrice");
            var reorder = row.Number("ReorderLevel");
            var qty = row.Number("OpeningQty");
            var cost = row.Number("OpeningCost") ?? purchase ?? existing?.PurchasePrice;
            if (existing is null && row.Text("NameAr") is null && row.Text("NameEn") is null)
                row.Errors.Add("Name (Arabic or English) is required / الاسم مطلوب");
            if (row.Errors.Count > 0) return false;

            var item = await masters.SaveItemAsync(existing?.Id, new ItemRequest(sku, row.Text("NameEn") ?? existing?.NameEn, row.Text("NameAr") ?? existing?.NameAr,
                type, group, unit, row.Text("Barcode") ?? existing?.Barcode, reorder ?? existing?.ReorderLevel, purchase ?? existing?.PurchasePrice,
                sales ?? existing?.SalesPrice, existing?.HsCode, existing?.IsActive ?? true, existing?.TrackBatches ?? false, existing?.TrackSerials ?? false,
                existing?.ReorderQuantity), token);

            if (qty is > 0)
            {
                if (type != ItemType.Stock)
                    throw new BusinessRuleException("import.opening_service", "Services have no stock: leave OpeningQty empty.", "الخدمات ليس لها مخزون: اترك الكمية الافتتاحية فارغة.");
                if (!canPostStock) throw new ForbiddenException("Opening quantities need the permission to post stock.");
                if (existing is not null && await db.Set<StockMovement>().AnyAsync(m => m.ItemId == item.Id, token))
                    throw new BusinessRuleException("import.opening_exists", "This item already has stock movements: use a stock count instead of an opening quantity.",
                        "لهذا الصنف حركات مخزون: استخدم الجرد بدلاً من الكمية الافتتاحية.");
                if (cost is null)
                    throw new BusinessRuleException("import.opening_cost", "Give OpeningCost or PurchasePrice to value the opening quantity.",
                        "أدخل تكلفة الوحدة الافتتاحية أو سعر الشراء لتقييم الكمية الافتتاحية.");
                var warehouse = row.Text("Warehouse") is { } wc
                    ? warehouses.FirstOrDefault(w => string.Equals(w.Code, wc, StringComparison.OrdinalIgnoreCase))
                    : warehouses.FirstOrDefault(w => w.Code == "MAIN") ?? warehouses.FirstOrDefault();
                if (warehouse is null)
                    throw new BusinessRuleException("import.warehouse", $"Warehouse '{row.Text("Warehouse")}' does not exist.", $"المستودع '{row.Text("Warehouse")}' غير موجود.");
                if (!opening.TryGetValue(warehouse.Id, out var lines)) opening[warehouse.Id] = lines = [];
                lines.Add(new AdjustmentLineRequest(item.Id, qty.Value, cost));
            }
            return existing is not null;
        }, async token =>
        {
            // One opening-stock document per warehouse, posted to the ledger like any adjustment.
            foreach (var (warehouseId, lines) in opening)
                await adjustments.CreateAsync(new AdjustmentRequest(warehouseId, clock.Today, AdjustmentReason.Opening, "Opening stock imported from Excel", lines), token);
        }, ct);
    }
}

using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Inventory.Endpoints;

/// <summary>Items at or below their reorder level.</summary>
internal sealed class ReorderScheduledReport(ErpDbContext db, ITenantContext tenant, IClock clock) : IScheduledReport
{
    public string Key => "inv.reorder";
    public string NameEn => "Items to reorder";
    public string NameAr => "أصناف تحتاج إعادة طلب";
    public string Permission => InventoryPermissions.ReportsRead;

    public async Task<ReportFile> RenderAsync(CancellationToken ct)
    {
        var rows = await InventoryEndpoints.StockAsync(db, tenant, ct, belowReorder: true);
        var content = CsvFile.Build(["SKU", "Item", "الصنف", "Unit", "On hand", "Reorder level", "Average cost"],
            rows.Select(x => (IReadOnlyList<object?>)[x.Sku, x.NameEn, x.NameAr, x.UomCode, x.Quantity, x.ReorderLevel, x.AverageCost]));
        return new ReportFile($"reorder_{clock.Today:yyyyMMdd}.csv", CsvFile.ContentType, content,
            $"{rows.Count} item(s) at or below their reorder level.", $"{rows.Count} صنف عند حد إعادة الطلب أو أقل.");
    }
}

using System.Text.Json;
using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Inventory.Endpoints;

/// <summary>Stock items at or below their reorder level, for the AI assistant.</summary>
internal sealed class LowStockAiTool(IInventoryQueries inventory) : IAiTool
{
    public string Name => "low_stock";
    public string Description => "Stock items at or below their reorder level: quantity on hand, reorder level and the usual quantity to reorder.";
    public string Permission => InventoryPermissions.ReportsRead;
    public string? Module => ModuleCodes.Inventory;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var rows = await inventory.GetReorderCandidatesAsync(ct);
        return new
        {
            items_to_reorder = rows.Count,
            items = rows.OrderBy(r => r.OnHand - r.ReorderLevel).Take(30).Select(r => new
            {
                item = r.NameEn, item_ar = r.NameAr, r.Sku, unit = r.UomCode, on_hand = r.OnHand, reorder_level = r.ReorderLevel, reorder_quantity = r.ReorderQuantity
            })
        };
    }
}

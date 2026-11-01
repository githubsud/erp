using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Inventory.Stock;

/// <summary>
/// Keeps goods on trust apart from our own goods: an item changes owner only while none of it is in stock (and nothing is valued), so
/// the weighted-average cost of a shop's item is always zero and our stock value never includes what we hold for others.
/// </summary>
internal sealed class ConsignmentItems(ErpDbContext db, ITenantContext tenant) : IConsignmentItems
{
    public async Task SetAsync(IReadOnlyList<Guid> itemIds, ConsignmentTerms? terms, CancellationToken ct = default)
    {
        db.RequireTransaction("Changing goods on trust");
        if (terms is not null)
        {
            if (terms.Commission < 0 || (terms.CommissionType == CommissionType.Percent && terms.Commission > 100))
                throw new BusinessRuleException("inv.commission_invalid", "The commission must be between 0 and 100%, or a positive amount per unit.",
                    "يجب أن تكون العمولة بين 0 و100% أو مبلغاً موجباً عن كل وحدة.");
        }
        var ids = itemIds.Distinct().ToList();
        var companyId = tenant.RequireCompanyId();
        // The valuation rows are locked as the stock ledger locks them, so a receipt cannot slip in between the stock check and the change of owner.
        var sorted = ids.OrderBy(id => id).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM inv.item_valuation WHERE item_id = ANY({sorted}) ORDER BY item_id FOR UPDATE", ct);
        var items = await db.Set<Item>().Where(i => ids.Contains(i.Id) && i.CompanyId == companyId).ToListAsync(ct);
        if (ids.FirstOrDefault(id => items.All(i => i.Id != id)) is var missing && missing != Guid.Empty) throw new NotFoundException("Item", missing);
        var held = await db.Set<ItemValuation>().Where(v => ids.Contains(v.ItemId) && (v.Quantity != 0 || v.Value != 0))
            .Select(v => v.ItemId).ToListAsync(ct);
        foreach (var item in items)
        {
            if (item.ItemType != ItemType.Stock)
                throw new BusinessRuleException("inv.consignment_service", $"{item.Sku} is a service item; only goods can be held on trust.",
                    $"{item.Sku} صنف خدمي؛ البضاعة فقط يمكن أن تكون أمانة.");
            if (item.ConsignorId != terms?.ConsignorId && held.Contains(item.Id))
                throw new BusinessRuleException("inv.consignment_in_stock",
                    $"{item.Sku} is in stock. Its owner can change only when none of it is in stock.",
                    $"الصنف {item.Sku} موجود في المخزون. لا يتغيّر مالكه إلا إذا لم يبقَ منه شيء في المخزون.");
            item.ConsignorId = terms?.ConsignorId;
            item.ConsignmentCommissionType = terms?.CommissionType;
            item.ConsignmentCommission = terms?.Commission;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ItemsOfAsync(Guid consignorId, CancellationToken ct = default) =>
        await db.Set<Item>().AsNoTracking().Where(i => i.CompanyId == tenant.RequireCompanyId() && i.ConsignorId == consignorId)
            .OrderBy(i => i.Sku).Select(i => i.Id).ToListAsync(ct);
}

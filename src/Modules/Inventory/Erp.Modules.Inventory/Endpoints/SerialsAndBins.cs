using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Inventory.Endpoints;

/// <summary>Where an item is kept in a warehouse (aisle-rack-shelf code), printed on pick lists.</summary>
public sealed class ItemBin : TenantEntity
{
    public Guid ItemId { get; init; }
    public Guid WarehouseId { get; init; }
    public required string BinCode { get; set; }
}

internal sealed class ItemBinConfiguration : IEntityTypeConfiguration<ItemBin>
{
    public void Configure(EntityTypeBuilder<ItemBin> b) => b.ToTable("item_bin", "inv");
}

public sealed record BinRequest(Guid WarehouseId, string? BinCode);

public sealed record BinDto(Guid WarehouseId, string WarehouseCode, string BinCode);

public sealed record SerialMovement(DateOnly Date, string SourceType, string SourceNo, Guid SourceId, string WarehouseCode, decimal Quantity);

public sealed record SerialInfo(string SerialNo, Guid ItemId, string Sku, string NameEn, string NameAr, bool InStock, string? WarehouseCode,
    IReadOnlyList<SerialMovement> History);

public sealed record SerialInStock(string SerialNo, Guid WarehouseId, string WarehouseCode);

internal static class SerialAndBinEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/inventory").WithTags("Inventory").RequireAuthorization();

        // Where is this unit, and what happened to it (received on…, delivered on…)?
        g.MapGet("/serials/{serialNo}", async (string serialNo, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var no = serialNo.Trim().ToUpperInvariant();
            var companyId = t.RequireCompanyId();
            var result = new List<SerialInfo>();
            foreach (var b in await (from b in db.Set<Batch>().AsNoTracking()
                                     join i in db.Set<Item>() on b.ItemId equals i.Id
                                     where b.CompanyId == companyId && b.BatchNo == no && i.TrackSerials
                                     select new { BatchId = b.Id, ItemId = i.Id, i.Sku, i.NameEn, i.NameAr }).ToListAsync(ct))
            {
                var history = await (from mb in db.Set<StockMovementBatch>().AsNoTracking()
                                     join m in db.Set<StockMovement>() on mb.MovementId equals m.Id
                                     join w in db.Set<Warehouse>() on m.WarehouseId equals w.Id
                                     where mb.BatchId == b.BatchId
                                     orderby m.MovementDate, m.CreatedAt
                                     select new SerialMovement(m.MovementDate, m.SourceType, m.SourceNo, m.SourceId, w.Code, mb.Quantity)).ToListAsync(ct);
                var here = await (from s in db.Set<BatchStock>().AsNoTracking()
                                  join w in db.Set<Warehouse>() on s.WarehouseId equals w.Id
                                  where s.BatchId == b.BatchId && s.Quantity > 0
                                  select w.Code).FirstOrDefaultAsync(ct);
                result.Add(new SerialInfo(no, b.ItemId, b.Sku, b.NameEn, b.NameAr, here is not null, here, history));
            }
            return result.Count > 0 ? result : throw new NotFoundException("Serial number", no);
        }).RequirePermission(InventoryPermissions.ItemsRead);

        // Serials on hand for an item (to pick from when delivering).
        g.MapGet("/items/{id:guid}/serials", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? warehouseId = null) =>
            await (from s in db.Set<BatchStock>().AsNoTracking()
                   join b in db.Set<Batch>() on s.BatchId equals b.Id
                   join w in db.Set<Warehouse>() on s.WarehouseId equals w.Id
                   where b.ItemId == id && b.CompanyId == t.RequireCompanyId() && s.Quantity > 0 && (warehouseId == null || s.WarehouseId == warehouseId)
                   orderby b.BatchNo
                   select new SerialInStock(b.BatchNo, w.Id, w.Code)).ToListAsync(ct)).RequirePermission(InventoryPermissions.ItemsRead);

        g.MapGet("/items/{id:guid}/bins", async (Guid id, ErpDbContext db, CancellationToken ct) =>
            await (from x in db.Set<ItemBin>().AsNoTracking()
                   join w in db.Set<Warehouse>() on x.WarehouseId equals w.Id
                   where x.ItemId == id
                   orderby w.Code
                   select new BinDto(x.WarehouseId, w.Code, x.BinCode)).ToListAsync(ct)).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPut("/items/{id:guid}/bins", async (Guid id, List<BinRequest> bins, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
        {
            var companyId = t.RequireCompanyId();
            if (!await db.Set<Item>().AnyAsync(i => i.Id == id && i.CompanyId == companyId, ct)) throw new NotFoundException("Item", id);
            var warehouses = await db.Set<Warehouse>().Where(w => w.CompanyId == companyId).Select(w => w.Id).ToListAsync(ct);
            var existing = await db.Set<ItemBin>().Where(x => x.ItemId == id).ToListAsync(ct);
            foreach (var r in bins.GroupBy(b => b.WarehouseId).Select(g => g.Last()))
            {
                if (!warehouses.Contains(r.WarehouseId)) throw new NotFoundException("Warehouse", r.WarehouseId);
                var current = existing.SingleOrDefault(x => x.WarehouseId == r.WarehouseId);
                var code = string.IsNullOrWhiteSpace(r.BinCode) ? null : r.BinCode.Trim().ToUpperInvariant();
                if (code is { Length: > 30 }) throw new BusinessRuleException("inv.bin_code", "Bin codes are up to 30 characters.", "رمز الموقع حتى 30 حرفاً.");
                if (code is null)
                {
                    if (current is not null) db.Remove(current);
                }
                else if (current is null) db.Add(new ItemBin { ItemId = id, WarehouseId = r.WarehouseId, BinCode = code });
                else current.BinCode = code;
            }
            await db.SaveChangesAsync(ct);
            return await (from x in db.Set<ItemBin>().AsNoTracking()
                          join w in db.Set<Warehouse>() on x.WarehouseId equals w.Id
                          where x.ItemId == id
                          select new BinDto(x.WarehouseId, w.Code, x.BinCode)).ToListAsync(ct);
        }).RequirePermission(InventoryPermissions.ItemsManage);
    }
}

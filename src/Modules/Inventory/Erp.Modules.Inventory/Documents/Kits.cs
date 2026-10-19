using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Inventory.Documents;

/// <summary>One component of a kit (gift hamper, first-aid box, PC bundle) per kit unit.</summary>
public sealed class KitComponent : TenantEntity
{
    public Guid KitItemId { get; init; }
    public Guid ComponentItemId { get; init; }
    public decimal Quantity { get; init; }
}

/// <summary>Builds kits: components leave stock at average cost and the kit enters at their total value.</summary>
public sealed class Assembly : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string AssemblyNo { get; init; }
    public DateOnly AssemblyDate { get; init; }
    public Guid WarehouseId { get; init; }
    public Guid KitItemId { get; init; }
    public decimal Quantity { get; init; }
    public decimal TotalValue { get; set; }
    public InventoryDocumentStatus Status { get; set; } = InventoryDocumentStatus.Posted;
    public Guid? JournalEntryId { get; set; }
    public string? Notes { get; init; }
}

internal sealed class KitComponentConfiguration : IEntityTypeConfiguration<KitComponent>
{
    public void Configure(EntityTypeBuilder<KitComponent> b)
    {
        b.ToTable("kit_component", "inv");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

internal sealed class AssemblyConfiguration : IEntityTypeConfiguration<Assembly>
{
    public void Configure(EntityTypeBuilder<Assembly> b)
    {
        b.ToTable("assembly", "inv");
        b.Property(x => x.Quantity).AsQuantity();
    }
}

public sealed record KitComponentRequest(Guid ItemId, decimal Quantity);

public sealed record KitComponentDto(Guid ItemId, string Sku, string NameEn, string NameAr, string UomCode, decimal Quantity, decimal AverageCost);

public sealed record AssemblyRequest(Guid KitItemId, Guid WarehouseId, DateOnly Date, decimal Quantity, string? Notes);

public sealed record AssemblyDto(Guid Id, string AssemblyNo, DateOnly AssemblyDate, Guid WarehouseId, Guid KitItemId, string Sku, string NameEn, string NameAr,
    decimal Quantity, decimal TotalValue, string Status, Guid? JournalEntryId);

internal sealed class KitService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IStockService stock, IPostingService posting)
{
    public const string SourceType = "assembly";

    public Task<List<KitComponentDto>> SaveComponentsAsync(Guid kitId, IReadOnlyList<KitComponentRequest> components, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var kit = await db.Set<Item>().SingleOrDefaultAsync(i => i.Id == kitId && i.CompanyId == companyId, token) ?? throw new NotFoundException("Item", kitId);
            if (kit.ItemType != ItemType.Stock || kit.TrackBatches)
                throw new BusinessRuleException("inv.kit_item", "A kit must be a stock item without batch or serial tracking.", "يجب أن تكون المجموعة صنف مخزون بدون تتبع دفعات أو أرقام تسلسلية.");
            if (components.Any(c => c.Quantity <= 0 || c.ItemId == kitId) || components.Select(c => c.ItemId).Distinct().Count() != components.Count)
                throw new BusinessRuleException("inv.kit_components", "List each component once with a positive quantity (not the kit itself).",
                    "أدرج كل مكوّن مرة واحدة بكمية موجبة (وليس المجموعة نفسها).");
            var ids = components.Select(c => c.ItemId).ToList();
            var parts = await db.Set<Item>().Where(i => ids.Contains(i.Id) && i.CompanyId == companyId).ToListAsync(token);
            if (parts.Count != ids.Count) throw new NotFoundException("Item", ids.Except(parts.Select(p => p.Id)).First());
            if (parts.Any(p => p.ItemType != ItemType.Stock))
                throw new BusinessRuleException("inv.kit_service", "Components must be stock items.", "يجب أن تكون المكونات أصناف مخزون.");
            // A kit of kits must not loop back to itself.
            if (await db.Set<KitComponent>().AnyAsync(k => ids.Contains(k.KitItemId) && k.ComponentItemId == kitId, token))
                throw new BusinessRuleException("inv.kit_cycle", "A component already contains this kit.", "أحد المكونات يحتوي هذه المجموعة.");
            db.RemoveRange(await db.Set<KitComponent>().Where(k => k.KitItemId == kitId).ToListAsync(token));
            foreach (var c in components) db.Add(new KitComponent { KitItemId = kitId, ComponentItemId = c.ItemId, Quantity = c.Quantity });
            await db.SaveChangesAsync(token);
            return await ComponentsAsync(kitId, token);
        }, ct);

    public async Task<List<KitComponentDto>> ComponentsAsync(Guid kitId, CancellationToken ct) =>
        await (from k in db.Set<KitComponent>().AsNoTracking()
               join i in db.Set<Item>() on k.ComponentItemId equals i.Id
               join u in db.Set<Uom>() on i.UomId equals u.Id
               join v in db.Set<ItemValuation>() on i.Id equals v.ItemId
               where k.KitItemId == kitId && i.CompanyId == tenant.RequireCompanyId()
               orderby i.Sku
               select new KitComponentDto(i.Id, i.Sku, i.NameEn, i.NameAr, u.Code, k.Quantity, v.AverageCost)).ToListAsync(ct);

    public Task<Assembly> AssembleAsync(AssemblyRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (r.Quantity <= 0) throw new BusinessRuleException("inv.assembly_quantity", "Enter how many kits to build.", "أدخل عدد المجموعات المراد تجميعها.");
            var components = await ComponentsAsync(r.KitItemId, token);
            if (components.Count == 0) throw new BusinessRuleException("inv.kit_empty", "This item has no components.", "لا توجد مكونات لهذا الصنف.");
            var assembly = new Assembly
            {
                CompanyId = tenant.RequireCompanyId(), AssemblyNo = await numbering.NextAsync(SourceType, "ASM", r.Date, ct: token), AssemblyDate = r.Date,
                WarehouseId = r.WarehouseId, KitItemId = r.KitItemId, Quantity = r.Quantity, Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim()
            };
            db.Add(assembly);
            var issued = await stock.PostAsync(new StockPosting(SourceType, assembly.Id, assembly.AssemblyNo, r.Date,
                components.Select(c => new StockLine(c.ItemId, r.WarehouseId, -c.Quantity * r.Quantity)).ToList()), token);
            var total = -issued.Sum(x => x.Value);
            var built = await stock.PostAsync(new StockPosting(SourceType, assembly.Id, assembly.AssemblyNo, r.Date,
                [new StockLine(r.KitItemId, r.WarehouseId, r.Quantity, Value: total, UnitCost: Math.Round(total / r.Quantity, 6, MidpointRounding.AwayFromZero))]), token);
            assembly.TotalValue = total;

            // Value only moves between item groups' inventory accounts; nothing to post when they are the same.
            var lines = issued.Concat(built).GroupBy(x => x.ItemGroupId).Select(g => (Group: g.Key, Net: g.Sum(x => x.Value))).Where(x => x.Net != 0)
                .Select(x => new PostingLine
                {
                    AccountRole = AccountRoles.Inventory, RoleKey = x.Group, Side = x.Net > 0 ? PostingSide.Debit : PostingSide.Credit, Amount = Math.Abs(x.Net)
                }).ToList();
            if (lines.Count > 0)
                assembly.JournalEntryId = (await posting.PostAsync(new PostingRequest
                {
                    SourceType = SourceType, SourceId = assembly.Id, SourceNo = assembly.AssemblyNo, PostingDate = r.Date,
                    MemoEn = $"Kit assembly {assembly.AssemblyNo}", MemoAr = $"تجميع مجموعة {assembly.AssemblyNo}", Lines = lines
                }, token)).JournalEntryId;
            await db.SaveChangesAsync(token);
            return assembly;
        }, ct);

    /// <summary>Takes the kits apart again: every movement is mirrored exactly.</summary>
    public Task<Assembly> ReverseAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var assembly = await db.Set<Assembly>().SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == tenant.RequireCompanyId(), token)
                           ?? throw new NotFoundException("Assembly", id);
            if (assembly.Status != InventoryDocumentStatus.Posted)
                throw new ConflictException("inv.not_posted", "This assembly is already reversed.", "تم عكس هذا التجميع مسبقاً.");
            await stock.ReverseAsync(SourceType, assembly.Id, r.Date, token);
            if (assembly.JournalEntryId is not null) await posting.ReverseAsync(SourceType, assembly.Id, r.Date, Guard.Required(r.Reason, "reason"), token);
            assembly.Status = InventoryDocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return assembly;
        }, ct);
}

internal static class KitEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/inventory").WithTags("Inventory").RequireAuthorization();
        g.MapGet("/items/{id:guid}/components", (Guid id, KitService s, CancellationToken ct) => s.ComponentsAsync(id, ct))
            .RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPut("/items/{id:guid}/components", (Guid id, List<KitComponentRequest> r, KitService s, CancellationToken ct) => s.SaveComponentsAsync(id, r, ct))
            .RequirePermission(InventoryPermissions.ItemsManage);
        g.MapGet("/assemblies", async (ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            await (from a in db.Set<Assembly>().AsNoTracking()
                   join i in db.Set<Item>() on a.KitItemId equals i.Id
                   where a.CompanyId == t.RequireCompanyId()
                   orderby a.AssemblyDate descending, a.AssemblyNo descending
                   select new AssemblyDto(a.Id, a.AssemblyNo, a.AssemblyDate, a.WarehouseId, a.KitItemId, i.Sku, i.NameEn, i.NameAr, a.Quantity, a.TotalValue,
                       a.Status.ToString(), a.JournalEntryId)).Take(200).ToListAsync(ct)).RequirePermission(InventoryPermissions.ItemsRead);
        g.MapPost("/assemblies", async (AssemblyRequest r, KitService s, CancellationToken ct) =>
        {
            var a = await s.AssembleAsync(r, ct);
            return new { a.Id, a.AssemblyNo, a.TotalValue };
        }).RequirePermission(InventoryPermissions.StockPost);
        g.MapPost("/assemblies/{id:guid}/reverse", async (Guid id, ReverseRequest r, KitService s, CancellationToken ct) =>
        {
            var a = await s.ReverseAsync(id, r, ct);
            return new { a.Id, Status = a.Status.ToString() };
        }).RequirePermission(InventoryPermissions.StockPost);
    }
}

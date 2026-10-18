using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory.Domain;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Inventory.Provisioning;

/// <summary>Seeds inventory settings, common units, a general item group and the main warehouse.</summary>
internal sealed class InventoryProvisioner(ErpDbContext db) : ITenantProvisioner
{
    public int Order => 30;

    private static readonly (string Code, string En, string Ar)[] Units =
    [
        ("PCS", "Piece", "قطعة"), ("BOX", "Box", "علبة"), ("CTN", "Carton", "كرتونة"), ("KG", "Kilogram", "كيلوغرام"),
        ("L", "Litre", "لتر"), ("M", "Metre", "متر"), ("SET", "Set", "طقم"), ("HR", "Hour", "ساعة")
    ];

    public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        db.Add(new InventorySettings { CompanyId = context.CompanyId });
        foreach (var (code, en, ar) in Units) db.Add(new Uom { CompanyId = context.CompanyId, Code = code, NameEn = en, NameAr = ar });
        db.Add(new ItemGroup { CompanyId = context.CompanyId, Code = "GEN", NameEn = "General", NameAr = "عام" });
        db.Add(new Warehouse { CompanyId = context.CompanyId, BranchId = context.BranchId, Code = "MAIN", NameEn = "Main Warehouse", NameAr = "المستودع الرئيسي" });
        return Task.CompletedTask;
    }
}

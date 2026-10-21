using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Pos;

/// <summary>A till to sell from, and the first sale through it.</summary>
internal sealed class PosGettingStarted(ErpDbContext db, ITenantContext tenant) : IGettingStartedProvider
{
    public string Permission => SalesPermissions.PosManage;
    public string? Module => ModuleCodes.Pos;

    public async Task<IReadOnlyList<GettingStartedStep>> GetStepsAsync(CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var till = await db.Set<PosRegister>().AnyAsync(r => r.CompanyId == companyId && r.IsActive, ct);
        var sold = await (from s in db.Set<PosSale>()
                          join sh in db.Set<PosShift>() on s.ShiftId equals sh.Id
                          join r in db.Set<PosRegister>() on sh.RegisterId equals r.Id
                          where r.CompanyId == companyId
                          select s.Id).AnyAsync(ct);
        return
        [
            new("till", 50, "Set up your till", "جهّز نقطة البيع",
                "Create a till for each counter; each has its own receipt numbers.", "أنشئ نقطة بيع لكل كاونتر؛ لكل منها أرقام إيصالات خاصة.",
                "/sales/pos", till),
            new("first-sale", 60, "Make your first sale", "قم بأول عملية بيع",
                "Open a shift, scan or tap a product, take payment and print the receipt.", "افتح وردية، امسح المنتج أو اختره، استلم المبلغ واطبع الإيصال.",
                "/sales/pos", sold)
        ];
    }
}

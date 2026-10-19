using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Domain;
using Erp.Modules.Sales.Pos;
using Erp.Modules.Sales.Tailoring;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Reports;

/// <summary>The owner's "how is today going": sales so far, cash that should be in the tills, refunds, the last shift's cash difference.</summary>
internal sealed class TodayDashboard(ErpDbContext db, ITenantContext tenant, IClock clock) : IDashboardProvider
{
    public string Permission => SalesPermissions.ReportsRead;
    public string? Module => ModuleCodes.Sales;

    public async Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var companyId = tenant.RequireCompanyId();
        // Till sales are counted from the till; invoices raised from the till (on account) are already in the till figures.
        var invoiced = await db.Set<SalesInvoice>().AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.InvoiceDate == today && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Reversed
                        && !db.Set<PosSale>().Any(p => p.SalesInvoiceId == i.Id))
            .SumAsync(i => (decimal?)i.TotalBase, ct) ?? 0;
        var registers = await db.Set<PosRegister>().AsNoTracking().Where(r => r.CompanyId == companyId).Select(r => r.Id).ToListAsync(ct);
        var shifts = await db.Set<PosShift>().AsNoTracking().Where(s => registers.Contains(s.RegisterId) && (s.BusinessDate == today || s.Status == ShiftStatus.Open))
            .ToListAsync(ct);
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var sales = await db.Set<PosSale>().AsNoTracking().Where(s => shiftIds.Contains(s.ShiftId)).ToListAsync(ct);
        var tillSales = sales.Where(s => s.RefundOfId == null).Sum(s => s.Total);
        var refunds = -sales.Where(s => s.RefundOfId != null).Sum(s => s.Total);
        var openShifts = shifts.Where(s => s.Status == ShiftStatus.Open).Select(s => s.Id).ToHashSet();
        var cashInTills = shifts.Where(s => openShifts.Contains(s.Id)).Sum(s => s.OpeningFloat)
                          + sales.Where(s => openShifts.Contains(s.ShiftId)).Sum(s => s.CashAmount - s.ChangeGiven);
        var lastClosed = await db.Set<PosShift>().AsNoTracking().Where(s => registers.Contains(s.RegisterId) && s.Status == ShiftStatus.Closed)
            .OrderByDescending(s => s.ClosedAt).FirstOrDefaultAsync(ct);
        var readyTailoring = await db.Set<TailoringOrder>().AsNoTracking().CountAsync(o => o.CompanyId == companyId && o.Status == TailoringStatus.Ready, ct);
        var tiles = new List<DashboardTile>
        {
            new("today", "today.sales", "Sales today", "مبيعات اليوم", tillSales + invoiced, "money", "/sales/invoices"),
            new("today", "today.refunds", "Refunds today", "مستردات اليوم", refunds, "money", "/sales/pos", Tone: refunds > 0 ? "warn" : null)
        };
        if (registers.Count > 0)
            tiles.Add(new("today", "today.cash", "Cash that should be in open tills", "النقد المفترض في الصناديق المفتوحة", cashInTills, "money", "/sales/pos"));
        if (lastClosed?.Difference is { } diff)
            tiles.Add(new("today", "today.last_shift", "Last shift cash difference", "فرق نقدية آخر وردية", diff, "money", "/sales/pos",
                $"Shift {lastClosed.ShiftNo}", $"الوردية {lastClosed.ShiftNo}", diff < 0 ? "bad" : diff > 0 ? "warn" : "ok"));
        if (readyTailoring > 0)
            tiles.Add(new("today", "today.tailoring_ready", "Orders ready, not collected", "طلبات جاهزة لم تُستلم", readyTailoring, "count", "/tailoring", Tone: "warn"));
        return tiles;
    }
}

using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Reports;

/// <summary>Cash and bank on hand, and this month's revenue and profit so far.</summary>
internal sealed class FinanceDashboard(ErpDbContext db, ITenantContext tenant, FinanceReports reports, FinancialStatements statements, IClock clock)
    : IDashboardProvider
{
    public string Permission => FinancePermissions.ReportsRead;

    public async Task<IReadOnlyList<DashboardTile>> GetTilesAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var cashAccounts = await db.Set<Account>().AsNoTracking()
            .Where(a => a.CompanyId == tenant.RequireCompanyId() && (a.Subtype == "cash" || a.Subtype == "bank")).Select(a => a.Id).ToListAsync(ct);
        var tb = await reports.TrialBalanceAsync(monthStart, today, ct);
        var cash = tb.Rows.Where(r => cashAccounts.Contains(r.AccountId)).Sum(r => r.Closing);
        var pl = await statements.ProfitAndLossAsync(monthStart, today, null, null, ct);
        return
        [
            new("finance", "fin.cash", "Cash and bank", "النقد والبنوك", cash, "money", "/finance/statements", Tone: cash < 0 ? "bad" : null),
            new("finance", "fin.revenue_mtd", "Revenue this month", "إيرادات هذا الشهر", pl.Revenue, "money", "/finance/statements"),
            new("finance", "fin.profit_mtd", "Profit this month", "ربح هذا الشهر", pl.NetProfit, "money", "/finance/statements",
                Tone: pl.NetProfit < 0 ? "bad" : "ok")
        ];
    }
}

using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Reports;

/// <summary>Income statement for a period, for the AI assistant.</summary>
internal sealed class ProfitAndLossAiTool(FinancialStatements statements, IClock clock) : IAiTool
{
    public string Name => "profit_and_loss";
    public string Description => "Revenue, cost of sales, gross profit, expenses by group and net profit for a period (from the accounts). Defaults to this month.";
    public string Permission => FinancePermissions.ReportsRead;
    public string ParametersSchema => """
        {"type":"object","properties":{"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"}}}
        """;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var today = clock.Today;
        var from = AiArgs.Date(args, "from", new DateOnly(today.Year, today.Month, 1));
        var to = AiArgs.Date(args, "to", today);
        var pl = await statements.ProfitAndLossAsync(from, to, null, null, ct);
        return new
        {
            from, to, pl.Revenue, pl.GrossProfit, pl.NetProfit,
            sections = pl.Sections.Select(s => new { section = s.NameEn, section_ar = s.NameAr, total = s.Total, kind = s.Kind })
        };
    }
}

/// <summary>Cash and bank balances today.</summary>
internal sealed class CashAiTool(ErpDbContext db, ITenantContext tenant, FinanceReports reports, IClock clock) : IAiTool
{
    public string Name => "cash_and_bank";
    public string Description => "Balance of each cash box and bank account today, and the total.";
    public string Permission => FinancePermissions.ReportsRead;

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var today = clock.Today;
        var accounts = await db.Set<Account>().AsNoTracking()
            .Where(a => a.CompanyId == tenant.RequireCompanyId() && (a.Subtype == "cash" || a.Subtype == "bank")).Select(a => a.Id).ToListAsync(ct);
        var tb = await reports.TrialBalanceAsync(new DateOnly(today.Year, today.Month, 1), today, ct);
        var rows = tb.Rows.Where(r => accounts.Contains(r.AccountId)).ToList();
        return new
        {
            as_of = today,
            total = rows.Sum(r => r.Closing),
            accounts = rows.Select(r => new { account = r.NameEn, account_ar = r.NameAr, balance = r.Closing })
        };
    }
}

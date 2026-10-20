using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Purchasing.Endpoints;

/// <summary>What the business owes its suppliers (payables aging), for the AI assistant.</summary>
internal sealed class SupplierBalancesAiTool(ErpDbContext db, ITenantContext tenant, IFinanceQueries finance) : IAiTool
{
    public string Name => "supplier_balances";
    public string Description => "Suppliers the business owes money to, largest first: total owed and the amount already past its due date. Also the total payables.";
    public string Permission => PurchasingPermissions.ReportsRead;
    public string? Module => ModuleCodes.Purchasing;
    public string ParametersSchema => """{"type":"object","properties":{"top":{"type":"integer","description":"how many suppliers, default 10"}}}""";

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var aging = await PurchasingEndpoints.AgingAsync(db, tenant, finance, ct);
        var top = AiArgs.Int(args, "top", 10, 1, 50);
        return new
        {
            as_of = aging.AsOf,
            total_payables = aging.Total,
            suppliers = aging.Rows.Where(r => r.Total > 0).OrderByDescending(r => r.Total).Take(top).Select(r => new
            {
                supplier = r.NameEn, supplier_ar = r.NameAr, owed = r.Total, past_due = r.Days1To30 + r.Days31To60 + r.Days61To90 + r.Over90
            })
        };
    }
}

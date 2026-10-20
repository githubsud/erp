using Erp.Infrastructure.Web;
using Erp.Modules.Ai;
using Erp.Modules.Core;
using Erp.Modules.Finance;
using Erp.Modules.HR;
using Erp.Modules.Inventory;
using Erp.Modules.Purchasing;
using Erp.Modules.Sales;

namespace Erp.Api;

/// <summary>The modules composed into this host. Order only affects endpoint registration.</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new CoreModule(),
        new FinanceModule(),
        new HrModule(),
        new InventoryModule(),
        new PurchasingModule(),
        new SalesModule(),
        new AiModule()
    ];
}

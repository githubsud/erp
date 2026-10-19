using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Banking;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Endpoints;
using Erp.Modules.Finance.Posting;
using Erp.Modules.Finance.Provisioning;
using Erp.Modules.Finance.Reports;
using Erp.Modules.Finance.Vouchers;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Finance;

public sealed class FinanceModule : IModule
{
    public string Name => "Finance";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<FiscalCalendar>();
        services.AddScoped<IPostingService, PostingService>();
        services.AddScoped<IPostingRuleRegistry, PostingRuleRegistry>();
        services.AddScoped<IFinanceQueries, FinanceQueries>();
        services.AddScoped<FinanceReports>();
        services.AddScoped<IScheduledReport, TrialBalanceScheduledReport>();
        services.AddScoped<IDashboardProvider, FinanceDashboard>();
        services.AddScoped<FinancialStatements>();
        services.AddScoped<CashFlowReport>();
        services.AddScoped<YearEndService>();
        services.AddScoped<Assets.FixedAssetService>();
        services.AddScoped<Budgets.BudgetService>();
        services.AddScoped<StatementsPdf>();
        services.AddScoped<BankReconciliationService>();
        services.AddScoped<JournalVoucherService>();
        services.AddScoped<ITenantProvisioner, FinanceProvisioner>();
        services.AddSingleton<IPermissionProvider, FinancePermissionProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        FinanceEndpoints.Map(app);
        Assets.FixedAssetEndpoints.Map(app);
        Budgets.BudgetEndpoints.Map(app);
    }
}

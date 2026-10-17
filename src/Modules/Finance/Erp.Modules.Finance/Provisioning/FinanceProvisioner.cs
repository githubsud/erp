using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Finance.Posting;
using Erp.SharedKernel.Application;

namespace Erp.Modules.Finance.Provisioning;

/// <summary>Seeds the chart of accounts, posting rules, a main cost centre and the current fiscal year.</summary>
internal sealed class FinanceProvisioner(ErpDbContext db, FiscalCalendar calendar) : ITenantProvisioner
{
    public int Order => 10;

    public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        var byCode = new Dictionary<string, Account>();
        foreach (var t in ChartOfAccountsTemplate.Accounts)
        {
            var account = new Account
            {
                CompanyId = context.CompanyId,
                ParentId = t.Parent is null ? null : byCode[t.Parent].Id,
                Code = t.Code,
                NameEn = t.NameEn,
                NameAr = t.NameAr,
                AccountType = t.Type,
                NormalBalance = t.Normal ?? ChartOfAccountsTemplate.DefaultNormal(t.Type),
                IsControl = t.Control is not null,
                ControlSubledger = t.Control,
                Subtype = t.Subtype,
                IsPostable = t.Postable
            };
            byCode[t.Code] = account;
            db.Add(account);
        }

        foreach (var (role, code) in ChartOfAccountsTemplate.RoleDefaults)
            db.Add(new PostingRule { CompanyId = context.CompanyId, AccountRole = role, AccountId = byCode[code].Id });

        db.Add(new CostCenter { CompanyId = context.CompanyId, Code = "MAIN", NameEn = "Main", NameAr = "الرئيسي" });

        var today = context.Today;
        var startYear = today.Month >= context.FiscalYearStartMonth ? today.Year : today.Year - 1;
        await calendar.CreateYearAsync(new DateOnly(startYear, context.FiscalYearStartMonth, 1), ct);
    }
}

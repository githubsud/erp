using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;

namespace Erp.Modules.HR.Provisioning;

/// <summary>Seeds Qatar payroll defaults: settings, pay components (with GL mappings), leave types and a department.</summary>
internal sealed class HrProvisioner(ErpDbContext db, IPostingRuleRegistry rules) : ITenantProvisioner
{
    public int Order => 20;

    private sealed record ComponentSeed(string Code, string En, string Ar, ComponentKind Kind, WpsField Field, bool Fixed, bool Prorate,
        bool Gratuity, string Role, string? AccountCode);

    private static readonly ComponentSeed[] Components =
    [
        new("BASIC", "Basic Salary", "الراتب الأساسي", ComponentKind.Earning, WpsField.Basic, true, true, true, AccountRoles.PayrollExpense, "5210"),
        new("HOUSING", "Housing Allowance", "بدل السكن", ComponentKind.Earning, WpsField.Housing, true, true, false, AccountRoles.PayrollExpense, "5220"),
        new("TRANSPORT", "Transport Allowance", "بدل المواصلات", ComponentKind.Earning, WpsField.Transport, true, true, false, AccountRoles.PayrollExpense, "5230"),
        new("FOOD", "Food Allowance", "بدل الطعام", ComponentKind.Earning, WpsField.Food, true, true, false, AccountRoles.PayrollExpense, "5240"),
        new("OTHER_ALW", "Other Allowance", "بدل آخر", ComponentKind.Earning, WpsField.ExtraIncome, true, true, false, AccountRoles.PayrollExpense, "5250"),
        new("OVERTIME", "Overtime", "العمل الإضافي", ComponentKind.Earning, WpsField.Overtime, false, false, false, AccountRoles.PayrollExpense, "5260"),
        new("EXPENSE_CLAIM", "Expense reimbursement", "تعويض مصروفات", ComponentKind.Earning, WpsField.ExtraIncome, false, false, false, AccountRoles.PayrollExpense, "5360"),
        new("BONUS", "Bonus / Incentive", "مكافأة / حافز", ComponentKind.Earning, WpsField.ExtraIncome, false, false, false, AccountRoles.PayrollExpense, "5265"),
        new("ADVANCE_RECOVERY", "Salary Advance Recovery", "استرداد سلفة راتب", ComponentKind.Deduction, WpsField.Deduction, false, false, false, AccountRoles.EmployeeAdvances, null),
        new("OTHER_DED", "Other Deduction", "خصم آخر", ComponentKind.Deduction, WpsField.Deduction, false, false, false, AccountRoles.PayrollDeduction, "4210"),
        new("AIR_TICKET", "Air ticket", "تذكرة السفر", ComponentKind.Earning, WpsField.ExtraIncome, false, false, false, AccountRoles.AirTicketProvision, null),
        new("SI_EMPLOYEE", "Social insurance (employee share)", "التأمينات الاجتماعية (حصة الموظف)", ComponentKind.Deduction, WpsField.Deduction, false, false,
            false, AccountRoles.SocialInsurancePayable, null)
    ];

    private static readonly (string Code, string En, string Ar, bool Paid, bool AffectsService, bool Annual)[] LeaveTypes =
    [
        ("ANNUAL", "Annual Leave", "إجازة سنوية", true, false, true),
        ("SICK", "Sick Leave", "إجازة مرضية", true, false, false),
        ("UNPAID", "Unpaid Leave", "إجازة بدون راتب", false, true, false),
        ("MATERNITY", "Maternity Leave", "إجازة أمومة", true, false, false),
        ("HAJJ", "Hajj Leave", "إجازة الحج", false, false, false),
        ("EMERGENCY", "Emergency Leave", "إجازة طارئة", true, false, true)
    ];

    public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken ct)
    {
        db.Add(new HrSettings { CompanyId = context.CompanyId });
        db.Add(new Department { CompanyId = context.CompanyId, Code = "GEN", NameEn = "General", NameAr = "عام" });

        foreach (var c in Components)
        {
            var component = new PayComponent
            {
                CompanyId = context.CompanyId,
                Code = c.Code,
                NameEn = c.En,
                NameAr = c.Ar,
                Kind = c.Kind,
                WpsField = c.Field,
                IsFixed = c.Fixed,
                ProrateByDays = c.Prorate,
                InGratuityBasis = c.Gratuity,
                InSocialInsuranceBasis = c.Code is "BASIC" or "HOUSING",
                PostingRole = c.Role,
                IsSystem = true
            };
            db.Add(component);
            if (c.AccountCode is not null)
                await rules.EnsureRuleAsync(c.Role, component.Id, c.AccountCode, ct);
        }

        foreach (var l in LeaveTypes)
            db.Add(new LeaveType
            {
                CompanyId = context.CompanyId,
                Code = l.Code,
                NameEn = l.En,
                NameAr = l.Ar,
                IsPaid = l.Paid,
                PayRule = l.Code == "SICK" ? LeavePayRule.SickBands : l.Paid ? LeavePayRule.Full : LeavePayRule.Unpaid,
                AffectsService = l.AffectsService,
                DeductsAnnualBalance = l.Annual,
                IsSystem = true
            });
    }
}

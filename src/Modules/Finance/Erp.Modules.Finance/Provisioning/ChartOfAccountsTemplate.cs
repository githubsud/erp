using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;

namespace Erp.Modules.Finance.Provisioning;

internal sealed record AccountTemplate(
    string Code,
    string? Parent,
    string NameEn,
    string NameAr,
    AccountType Type,
    bool Postable = true,
    NormalBalance? Normal = null,
    ControlSubledger? Control = null,
    string? Subtype = null);

/// <summary>
/// IFRS-aligned bilingual chart of accounts for a Qatar SME, with the payroll accounts the People bundle needs.
/// Tenants extend it freely; codes are stable references for posting rules.
/// </summary>
internal static class ChartOfAccountsTemplate
{
    private const AccountType A = AccountType.Asset, L = AccountType.Liability, E = AccountType.Equity,
        I = AccountType.Income, X = AccountType.Expense;

    public static readonly IReadOnlyList<AccountTemplate> Accounts =
    [
        new("1", null, "Assets", "الأصول", A, false),
        new("11", "1", "Current Assets", "الأصول المتداولة", A, false),
        new("1110", "11", "Cash on Hand", "النقد في الصندوق", A, Subtype: "cash"),
        new("1120", "11", "Bank - Current Account", "البنك - الحساب الجاري", A, Subtype: "bank"),
        new("1130", "11", "Accounts Receivable", "الذمم المدينة", A, Control: ControlSubledger.Customer),
        new("1140", "11", "Post-dated Cheques Receivable", "شيكات آجلة برسم التحصيل", A),
        new("1150", "11", "Inventory", "المخزون", A),
        new("1160", "11", "Employee Advances and Loans", "سلف وقروض الموظفين", A, Control: ControlSubledger.Employee),
        new("1170", "11", "Prepaid Expenses", "مصروفات مدفوعة مقدماً", A),
        new("1180", "11", "Refundable Deposits", "تأمينات مستردة", A),
        new("1190", "11", "Landed Cost Clearing", "حساب وسيط تكاليف الاستيراد", A),
        new("12", "1", "Non-current Assets", "الأصول غير المتداولة", A, false),
        new("1210", "12", "Property, Plant and Equipment", "الممتلكات والمنشآت والمعدات", A),
        new("1220", "12", "Accumulated Depreciation", "مجمع الإهلاك", A, Normal: NormalBalance.Credit),

        new("2", null, "Liabilities", "الالتزامات", L, false),
        new("21", "2", "Current Liabilities", "الالتزامات المتداولة", L, false),
        new("2110", "21", "Accounts Payable", "الذمم الدائنة", L, Control: ControlSubledger.Supplier),
        new("2120", "21", "Post-dated Cheques Payable", "شيكات آجلة مستحقة الدفع", L),
        new("2125", "21", "Goods Received Not Invoiced", "بضاعة مستلمة لم تصل فواتيرها", L),
        new("2130", "21", "Accrued Expenses", "مصروفات مستحقة", L),
        new("2140", "21", "Salaries Payable", "رواتب مستحقة الدفع", L),
        new("2150", "21", "Leave Salary Provision", "مخصص رواتب الإجازات", L),
        new("2160", "21", "Air Ticket Provision", "مخصص تذاكر السفر", L),
        new("2170", "21", "Withholding Tax Payable", "ضريبة الاستقطاع المستحقة", L),
        new("2180", "21", "Social Insurance Payable (GRSIA)", "اشتراكات التأمينات الاجتماعية المستحقة", L),
        new("2190", "21", "Corporate Income Tax Payable", "ضريبة الدخل المستحقة", L),
        new("22", "2", "Non-current Liabilities", "الالتزامات غير المتداولة", L, false),
        new("2210", "22", "End of Service Benefits Provision", "مخصص مكافأة نهاية الخدمة", L),

        new("3", null, "Equity", "حقوق الملكية", E, false),
        new("3110", "3", "Share Capital", "رأس المال", E),
        new("3120", "3", "Legal Reserve", "الاحتياطي القانوني", E),
        new("3130", "3", "Retained Earnings", "الأرباح المحتجزة", E, Subtype: "retained_earnings"),
        new("3150", "3", "Opening Balance Equity", "حقوق ملكية الأرصدة الافتتاحية", E),

        new("4", null, "Income", "الإيرادات", I, false),
        new("4110", "4", "Sales Revenue", "إيرادات المبيعات", I),
        new("4120", "4", "Service Revenue", "إيرادات الخدمات", I),
        new("4130", "4", "Sales Returns and Discounts", "مردودات وخصومات المبيعات", I, Normal: NormalBalance.Debit),
        new("4210", "4", "Other Income", "إيرادات أخرى", I),
        new("4220", "4", "Foreign Exchange Differences", "فروقات أسعار الصرف", I),
        new("4230", "4", "Gain or Loss on Disposal of Assets", "أرباح أو خسائر بيع الأصول", I),

        new("5", null, "Expenses", "المصروفات", X, false),
        new("51", "5", "Cost of Sales", "تكلفة المبيعات", X, false, Subtype: "cost_of_sales"),
        new("5110", "51", "Cost of Goods Sold", "تكلفة البضاعة المباعة", X),
        new("5120", "51", "Inventory Adjustments", "تسويات المخزون", X),
        new("52", "5", "Staff Costs", "تكاليف الموظفين", X, false),
        new("5210", "52", "Basic Salaries", "الرواتب الأساسية", X),
        new("5220", "52", "Housing Allowance", "بدل السكن", X),
        new("5230", "52", "Transport Allowance", "بدل المواصلات", X),
        new("5240", "52", "Food Allowance", "بدل الطعام", X),
        new("5250", "52", "Other Allowances", "بدلات أخرى", X),
        new("5260", "52", "Overtime", "العمل الإضافي", X),
        new("5265", "52", "Bonuses and Incentives", "المكافآت والحوافز", X),
        new("5270", "52", "End of Service Benefits Expense", "مصروف مكافأة نهاية الخدمة", X),
        new("5280", "52", "Leave Salary Expense", "مصروف رواتب الإجازات", X),
        new("5290", "52", "Air Ticket Expense", "مصروف تذاكر السفر", X),
        new("5295", "52", "Social Insurance - Employer Contribution", "حصة صاحب العمل في التأمينات الاجتماعية", X),
        new("53", "5", "General and Administrative Expenses", "المصروفات العمومية والإدارية", X, false),
        new("5310", "53", "Rent", "الإيجار", X),
        new("5320", "53", "Utilities", "المرافق", X),
        new("5330", "53", "Government Fees and Renewals", "الرسوم الحكومية والتجديدات", X),
        new("5340", "53", "Bank Charges", "الرسوم البنكية", X),
        new("5350", "53", "Depreciation", "الإهلاك", X),
        new("5360", "53", "Purchased Services and Supplies", "خدمات ومستلزمات مشتراة", X),
        new("5390", "53", "Rounding Differences", "فروقات التقريب", X)
    ];

    /// <summary>Default account for each posting role.</summary>
    public static readonly IReadOnlyList<(string Role, string Code)> RoleDefaults =
    [
        (AccountRoles.Bank, "1120"),
        (AccountRoles.Cash, "1110"),
        (AccountRoles.Rounding, "5390"),
        (AccountRoles.OpeningBalanceEquity, "3150"),
        (AccountRoles.ExchangeDifference, "4220"),
        (AccountRoles.PayrollExpense, "5250"),
        (AccountRoles.PayrollDeduction, "4210"),
        (AccountRoles.SalariesPayable, "2140"),
        (AccountRoles.EmployeeAdvances, "1160"),
        (AccountRoles.GratuityExpense, "5270"),
        (AccountRoles.GratuityProvision, "2210"),
        (AccountRoles.LeaveSalaryExpense, "5280"),
        (AccountRoles.LeaveSalaryProvision, "2150"),
        (AccountRoles.AirTicketExpense, "5290"),
        (AccountRoles.AirTicketProvision, "2160"),
        (AccountRoles.SocialInsuranceExpense, "5295"),
        (AccountRoles.SocialInsurancePayable, "2180"),
        (AccountRoles.Inventory, "1150"),
        (AccountRoles.CostOfSales, "5110"),
        (AccountRoles.StockAdjustment, "5120"),
        (AccountRoles.GoodsReceivedNotInvoiced, "2125"),
        (AccountRoles.AccountsPayable, "2110"),
        (AccountRoles.WithholdingTaxPayable, "2170"),
        (AccountRoles.PdcPayable, "2120"),
        (AccountRoles.LandedCostClearing, "1190"),
        (AccountRoles.PurchaseExpense, "5360"),
        (AccountRoles.AccountsReceivable, "1130"),
        (AccountRoles.SalesRevenue, "4110"),
        (AccountRoles.ServiceRevenue, "4120"),
        (AccountRoles.SalesReturns, "4130"),
        (AccountRoles.PdcReceivable, "1140")
    ];

    public static NormalBalance DefaultNormal(AccountType type) =>
        type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit;
}

using Erp.Infrastructure.Import;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

/// <summary>Customers from Excel: new codes are created, existing ones updated (blank cells keep the current value).</summary>
internal sealed class CustomerImportService(ErpDbContext db, ITenantContext tenant, CustomerService customers)
{
    internal static readonly ImportColumn[] Columns =
    [
        new("Code", "Customer code", "رمز العميل", true),
        new("NameAr", "Name (Arabic)", "الاسم بالعربية"),
        new("NameEn", "Name (English)", "الاسم بالإنجليزية"),
        new("Phone", "Mobile / phone", "الجوال / الهاتف"),
        new("Email", "Email", "البريد الإلكتروني"),
        new("CreditLimit", "Credit limit (QAR, blank: none)", "حد الائتمان (ريال، فارغ: بدون)"),
        new("PaymentTermsDays", "Payment terms (days, blank: 30)", "مدة السداد (أيام، فارغ: 30)"),
        new("AddressAr", "Address (Arabic)", "العنوان بالعربية"),
        new("AddressEn", "Address (English)", "العنوان بالإنجليزية"),
        new("CrNo", "Commercial registration no.", "رقم السجل التجاري"),
        new("TaxCardNo", "Tax card no.", "رقم البطاقة الضريبية"),
        new("Country", "Country code (blank: QA)", "رمز الدولة (فارغ: QA)")
    ];

    public static byte[] Template() => ExcelImport.Template("Customers", Columns,
        [
            ["C-0001", "مطعم الريان", "Al Rayyan Restaurant", "+97455501234", "accounts@rayyan.example", "5000", "30", "الريان، شارع 340", "Al Rayyan, St 340", "123456", "", ""],
            ["C-0002", "فاطمة أحمد", "Fatima Ahmed", "+97466601234", "", "", "0", "", "", "", "", ""]
        ],
        ["One customer per row. Existing customer codes are updated (empty cells keep the current value); new codes are created.",
         "Payment terms 0 means cash customers. Phone numbers are used for WhatsApp messages."],
        ["عميل واحد في كل صف. العملاء الموجودون بنفس الرمز تُحدّث بياناتهم (الخلايا الفارغة تبقي القيمة الحالية)، والرموز الجديدة تُضاف.",
         "مدة السداد 0 تعني عميلاً نقدياً. أرقام الجوال تُستخدم لرسائل واتساب."]);

    public async Task<ImportSummary> ImportAsync(Stream file, bool dryRun, CancellationToken ct)
    {
        var rows = ExcelImport.Read(file, Columns);
        var companyId = tenant.RequireCompanyId();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return await ExcelImport.RunAsync(db, Columns, rows, dryRun, async (row, token) =>
        {
            var code = row.Text("Code")!.ToUpperInvariant();
            if (!seen.Add(code)) row.Errors.Add($"Customer code {code} appears twice in the file / الرمز مكرر في الملف");
            var c = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Code == code, token);
            var limit = row.Number("CreditLimit", 2);
            var terms = row.Number("PaymentTermsDays", 0);
            if (c is null && row.Text("NameAr") is null && row.Text("NameEn") is null) row.Errors.Add("Name (Arabic or English) is required / الاسم مطلوب");
            if (row.Errors.Count > 0) return false;
            await customers.SaveAsync(c?.Id, new CustomerRequest(code, row.Text("NameEn") ?? c?.NameEn, row.Text("NameAr") ?? c?.NameAr,
                row.Text("Country") ?? c?.Country, row.Text("TaxCardNo") ?? c?.TaxCardNo, row.Text("CrNo") ?? c?.CrNo, row.Text("Email") ?? c?.Email,
                row.Text("Phone") ?? c?.Phone, row.Text("AddressEn") ?? c?.AddressEn, row.Text("AddressAr") ?? c?.AddressAr,
                terms is { } t ? (int)t : c?.PaymentTermsDays ?? 30, limit ?? c?.CreditLimit, c?.PriceListId, c?.SalespersonUserId, c?.IsActive ?? true,
                c?.Currency), token);
            return c is not null;
        }, null, ct);
    }
}

using Erp.Infrastructure.Import;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Purchasing.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Documents;

/// <summary>Suppliers from Excel: new codes are created, existing ones updated (blank cells keep the current value).</summary>
internal sealed class SupplierImportService(ErpDbContext db, ITenantContext tenant, SupplierService suppliers)
{
    internal static readonly ImportColumn[] Columns =
    [
        new("Code", "Supplier code", "رمز المورد", true),
        new("NameAr", "Name (Arabic)", "الاسم بالعربية"),
        new("NameEn", "Name (English)", "الاسم بالإنجليزية"),
        new("Phone", "Mobile / phone", "الجوال / الهاتف"),
        new("Email", "Email", "البريد الإلكتروني"),
        new("PaymentTermsDays", "Payment terms (days, blank: 30)", "مدة السداد (أيام، فارغ: 30)"),
        new("CrNo", "Commercial registration no.", "رقم السجل التجاري"),
        new("TaxCardNo", "Tax card no.", "رقم البطاقة الضريبية"),
        new("Country", "Country code (blank: QA)", "رمز الدولة (فارغ: QA)"),
        new("Resident", "Resident in Qatar (Y/N, blank: Y)", "مقيم في قطر (Y/N، فارغ: Y)", Choices: ["Y", "N"])
    ];

    public static byte[] Template() => ExcelImport.Template("Suppliers", Columns,
        [
            ["S-0001", "شركة الخليج للمواد الغذائية", "Gulf Food Supplies", "+97444401234", "orders@gulffood.example", "30", "234567", "", "", "Y"],
            ["S-0002", "مخبز الدوحة", "Doha Bakery", "+97455507777", "", "0", "", "", "", "Y"]
        ],
        ["One supplier per row. Existing supplier codes are updated (empty cells keep the current value); new codes are created.",
         "Suppliers outside Qatar (Resident = N) may need withholding tax on services; set it on the supplier afterwards."],
        ["مورد واحد في كل صف. الموردون الموجودون بنفس الرمز تُحدّث بياناتهم (الخلايا الفارغة تبقي القيمة الحالية)، والرموز الجديدة تُضاف.",
         "الموردون من خارج قطر (مقيم = N) قد تلزمهم ضريبة الاستقطاع على الخدمات؛ اضبطها في بطاقة المورد بعد الاستيراد."]);

    public async Task<ImportSummary> ImportAsync(Stream file, bool dryRun, CancellationToken ct)
    {
        var rows = ExcelImport.Read(file, Columns);
        var companyId = tenant.RequireCompanyId();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return await ExcelImport.RunAsync(db, Columns, rows, dryRun, async (row, token) =>
        {
            var code = row.Text("Code")!.ToUpperInvariant();
            if (!seen.Add(code)) row.Errors.Add($"Supplier code {code} appears twice in the file / الرمز مكرر في الملف");
            var s = await db.Set<Supplier>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Code == code, token);
            var terms = row.Number("PaymentTermsDays", 0);
            if (s is null && row.Text("NameAr") is null && row.Text("NameEn") is null) row.Errors.Add("Name (Arabic or English) is required / الاسم مطلوب");
            if (row.Errors.Count > 0) return false;
            await suppliers.SaveAsync(s?.Id, new SupplierRequest(code, row.Text("NameEn") ?? s?.NameEn, row.Text("NameAr") ?? s?.NameAr,
                row.Text("Country") ?? s?.Country, row.YesNo("Resident", s?.IsResident ?? true), s?.HasPermanentEstablishment ?? false,
                row.Text("TaxCardNo") ?? s?.TaxCardNo, row.Text("CrNo") ?? s?.CrNo, row.Text("Email") ?? s?.Email, row.Text("Phone") ?? s?.Phone,
                terms is { } t ? (int)t : s?.PaymentTermsDays ?? 30, s?.DefaultWhtCategory ?? WhtCategory.None, s?.IsActive ?? true, s?.Currency), token);
            return s is not null;
        }, null, ct);
    }
}

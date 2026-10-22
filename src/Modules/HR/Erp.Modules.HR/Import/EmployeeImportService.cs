using System.Globalization;
using ClosedXML.Excel;
using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Documents;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.People;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Import;

public sealed record ImportRowResult(int Row, string? EmpNo, string? Name, bool Ok, IReadOnlyList<string> Errors);

public sealed record ImportResult(bool DryRun, bool Committed, int TotalRows, int ValidRows, IReadOnlyList<ImportRowResult> Rows);

/// <summary>
/// Onboarding from Excel: employees with their contract and document expiries in one sheet. Every row goes through
/// the same services (and validation) as the screens. The whole file is all-or-nothing: rows run inside savepoints so
/// every error is reported, and nothing is committed unless all rows are valid and it is not a dry run.
/// </summary>
internal sealed class EmployeeImportService(ErpDbContext db, EmployeeService employees, ContractService contracts, DocumentService documents)
{
    public const int MaxRows = 2000;
    private const int FirstDataRow = 3;

    internal sealed record Column(string Key, string En, string Ar, bool Required = false);

    internal static readonly Column[] Columns =
    [
        new("EmpNo", "Employee no.", "الرقم الوظيفي", true),
        new("NameEn", "Name (English, as on QID)", "الاسم بالإنجليزية"),
        new("NameAr", "Name (Arabic, as on QID)", "الاسم بالعربية"),
        new("Nationality", "Nationality (ISO code: QA, IN, NP, PH…)", "الجنسية (رمز الدولة)", true),
        new("Gender", "Gender (M/F)", "الجنس (M/F)", true),
        new("DateOfBirth", "Date of birth", "تاريخ الميلاد"),
        new("JoiningDate", "Joining date", "تاريخ الالتحاق", true),
        new("JobTitleEn", "Job title (English)", "المسمى الوظيفي (إنجليزي)"),
        new("JobTitleAr", "Job title (Arabic)", "المسمى الوظيفي (عربي)"),
        new("Mobile", "Mobile", "الجوال"),
        new("Email", "Email", "البريد الإلكتروني"),
        new("QID", "QID (11 digits)", "الرقم الشخصي"),
        new("VisaId", "Visa / residence ID", "رقم التأشيرة"),
        new("PassportNo", "Passport no.", "رقم الجواز"),
        new("IBAN", "IBAN", "الآيبان"),
        new("BankShortName", "Bank short name", "رمز البنك"),
        new("InWps", "Paid through WPS (Y/N)", "عبر حماية الأجور (Y/N)"),
        new("SocialInsurance", "Social insurance (None/Grsia/GccHome)", "التأمينات الاجتماعية"),
        new("Basic", "Basic salary", "الراتب الأساسي", true),
        new("Housing", "Housing allowance", "بدل السكن"),
        new("Transport", "Transport allowance", "بدل المواصلات"),
        new("Food", "Food allowance", "بدل الطعام"),
        new("OtherAllowance", "Other allowance", "بدل آخر"),
        new("AccommodationProvided", "Accommodation provided (Y/N)", "سكن مقدم (Y/N)"),
        new("FoodProvided", "Food provided (Y/N)", "طعام مقدم (Y/N)"),
        new("ContractType", "Contract type (Unlimited/Limited)", "نوع العقد"),
        new("OpeningGratuity", "Opening gratuity liability", "رصيد المكافأة الافتتاحي"),
        new("QidExpiry", "QID expiry", "انتهاء البطاقة الشخصية"),
        new("PassportExpiry", "Passport expiry", "انتهاء الجواز"),
        new("WorkPermitExpiry", "Work permit expiry", "انتهاء تصريح العمل"),
        new("HealthCardExpiry", "Health card expiry", "انتهاء البطاقة الصحية")
    ];

    public async Task<ImportResult> ImportAsync(Stream file, bool dryRun, CancellationToken ct)
    {
        List<Dictionary<string, string?>> rows;
        try
        {
            rows = Read(file);
        }
        catch (Exception ex) when (ex is not BusinessRuleException)
        {
            throw new BusinessRuleException("import.unreadable", "The file could not be read. Use the Excel template (.xlsx).",
                "تعذر قراءة الملف. استخدم قالب إكسل (.xlsx).");
        }
        if (rows.Count == 0)
            throw new BusinessRuleException("import.empty", "The file has no employee rows.", "لا يحتوي الملف على بيانات موظفين.");
        if (rows.Count > MaxRows)
            throw new BusinessRuleException("import.too_many", $"At most {MaxRows} rows can be imported at once.", $"الحد الأقصى {MaxRows} صف في كل مرة.");

        var results = new List<ImportRowResult>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            var rowNo = int.Parse(row["__row"]!);
            var errors = new List<string>();
            await transaction.CreateSavepointAsync("import_row", ct);
            try
            {
                await ImportRowAsync(row, errors, ct);
            }
            catch (ErpException ex)
            {
                errors.Add(ex.Message);
            }
            catch (DbUpdateException ex)
            {
                errors.Add(PostgresErrors.Find(ex)?.MessageText ?? ex.Message);
            }

            if (errors.Count > 0)
            {
                await transaction.RollbackToSavepointAsync("import_row", ct);
                db.ChangeTracker.Clear();
            }
            else
            {
                await transaction.ReleaseSavepointAsync("import_row", ct);
            }
            results.Add(new ImportRowResult(rowNo, row.GetValueOrDefault("EmpNo"), row.GetValueOrDefault("NameEn") ?? row.GetValueOrDefault("NameAr"),
                errors.Count == 0, errors));
        }

        var valid = results.Count(r => r.Ok);
        var commit = !dryRun && valid == results.Count;
        if (commit) await transaction.CommitAsync(ct);
        else await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return new ImportResult(dryRun, commit, results.Count, valid, results);
    }

    private async Task ImportRowAsync(Dictionary<string, string?> row, List<string> errors, CancellationToken ct)
    {
        foreach (var c in Columns.Where(c => c.Required && string.IsNullOrWhiteSpace(row.GetValueOrDefault(c.Key))))
            errors.Add($"{c.En} is required / {c.Ar} مطلوب");
        var joining = Date(row, "JoiningDate", errors);
        var basic = Number(row, "Basic", errors);
        if (errors.Count > 0 || joining is null) return;

        var gender = Text(row, "Gender")?.ToUpperInvariant() switch
        {
            "F" or "FEMALE" or "أنثى" => Gender.Female,
            _ => Gender.Male
        };
        var scheme = Enum.TryParse<SocialInsuranceScheme>(Text(row, "SocialInsurance"), true, out var s) ? s : SocialInsuranceScheme.None;
        var employee = await employees.CreateAsync(new EmployeeRequest(
            Text(row, "EmpNo")!, Text(row, "NameEn"), Text(row, "NameAr"), Text(row, "Nationality")!, gender, Date(row, "DateOfBirth", errors),
            joining.Value, null, null, null, Text(row, "JobTitleEn"), Text(row, "JobTitleAr"), Text(row, "Mobile"), Text(row, "Email"),
            Text(row, "QID"), Text(row, "VisaId"), Text(row, "PassportNo"), Text(row, "BankShortName"), Text(row, "IBAN"),
            YesNo(row, "InWps", true), scheme, Number(row, "OpeningGratuity", errors) ?? 0), ct);

        var components = new List<ContractComponentRequest> { new("BASIC", basic!.Value) };
        foreach (var (key, code) in new[] { ("Housing", "HOUSING"), ("Transport", "TRANSPORT"), ("Food", "FOOD"), ("OtherAllowance", "OTHER_ALW") })
            if (Number(row, key, errors) is > 0 and var amount) components.Add(new(code, amount));
        var contractType = Text(row, "ContractType")?.StartsWith("L", StringComparison.OrdinalIgnoreCase) == true ? ContractType.Limited : ContractType.Unlimited;
        await contracts.CreateAsync(employee.Id, new ContractRequest(joining.Value, null, contractType, 0, YesNo(row, "AccommodationProvided", false),
            YesNo(row, "FoodProvided", false), null, GratuityBasis.Basic, null, components), ct);

        foreach (var (key, type) in new[]
                 {
                     ("QidExpiry", EmployeeDocumentType.Qid), ("PassportExpiry", EmployeeDocumentType.Passport),
                     ("WorkPermitExpiry", EmployeeDocumentType.WorkPermit), ("HealthCardExpiry", EmployeeDocumentType.HealthCard)
                 })
            if (Date(row, key, errors) is { } expiry)
                await documents.AddEmployeeDocumentAsync(employee.Id, new EmployeeDocumentRequest(type,
                    type == EmployeeDocumentType.Qid ? Text(row, "QID") : type == EmployeeDocumentType.Passport ? Text(row, "PassportNo") : null,
                    null, expiry, null), ct);

        if (errors.Count > 0)
            throw new BusinessRuleException("import.row", string.Join("; ", errors));
    }

    private static List<Dictionary<string, string?>> Read(Stream file)
    {
        using var workbook = Erp.Infrastructure.Import.ExcelImport.Open(file);
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == "Employees") ?? workbook.Worksheet(1);
        var header = sheet.Row(1);
        var map = new Dictionary<int, string>();
        foreach (var cell in header.CellsUsed())
        {
            var key = Columns.FirstOrDefault(c => string.Equals(c.Key, cell.GetString().Trim(), StringComparison.OrdinalIgnoreCase))?.Key;
            if (key is not null) map[cell.Address.ColumnNumber] = key;
        }
        if (!map.ContainsValue("EmpNo"))
            throw new BusinessRuleException("import.template", "The first row must contain the template column names (EmpNo, NameEn, …).",
                "يجب أن يحتوي الصف الأول على أسماء أعمدة القالب.");

        var rows = new List<Dictionary<string, string?>>();
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = FirstDataRow; r <= last; r++)
        {
            var values = new Dictionary<string, string?> { ["__row"] = r.ToString(CultureInfo.InvariantCulture) };
            foreach (var (col, key) in map)
            {
                var cell = sheet.Cell(r, col);
                values[key] = cell.IsEmpty() ? null
                    : cell.DataType == XLDataType.DateTime ? DateOnly.FromDateTime(cell.GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : cell.DataType == XLDataType.Number ? cell.GetDouble().ToString(CultureInfo.InvariantCulture)
                    : cell.GetString().Trim();
            }
            if (map.Values.Any(k => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(k))))
                rows.Add(values);
        }
        return rows;
    }

    private static string? Text(Dictionary<string, string?> row, string key) =>
        row.GetValueOrDefault(key) is { Length: > 0 } v ? v.Trim() : null;

    private static bool YesNo(Dictionary<string, string?> row, string key, bool defaultValue) =>
        Text(row, key)?.ToUpperInvariant() switch
        {
            "Y" or "YES" or "TRUE" or "1" or "نعم" => true,
            "N" or "NO" or "FALSE" or "0" or "لا" => false,
            _ => defaultValue
        };

    private static decimal? Number(Dictionary<string, string?> row, string key, List<string> errors)
    {
        var text = Text(row, key);
        if (text is null) return null;
        if (decimal.TryParse(text.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return Money.Round(value);
        errors.Add($"{key}: '{text}' is not a number");
        return null;
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy"];

    private static DateOnly? Date(Dictionary<string, string?> row, string key, List<string> errors)
    {
        var text = Text(row, key);
        if (text is null) return null;
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        errors.Add($"{key}: '{text}' is not a date (use dd/MM/yyyy)");
        return null;
    }

    /// <summary>Excel template: column keys, bilingual hints, a sample row, an instructions sheet and drop-down lists.</summary>
    public static byte[] Template()
    {
        using var workbook = new XLWorkbook();
        workbook.RightToLeft = false;
        var sheet = workbook.Worksheets.Add("Employees");
        for (var i = 0; i < Columns.Length; i++)
        {
            var c = Columns[i];
            var key = sheet.Cell(1, i + 1);
            key.Value = c.Key;
            key.Style.Font.Bold = true;
            key.Style.Fill.BackgroundColor = c.Required ? XLColor.FromHtml("#0F5C4D") : XLColor.FromHtml("#3D7A6E");
            key.Style.Font.FontColor = XLColor.White;
            var hint = sheet.Cell(2, i + 1);
            hint.Value = $"{c.En}\n{c.Ar}";
            hint.Style.Alignment.WrapText = true;
            hint.Style.Font.FontColor = XLColor.FromHtml("#5F6B6A");
            hint.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F4");
        }

        string[] sample =
        [
            "E001", "Ramesh Kumar", "راميش كومار", "IN", "M", "15/05/1990", "01/03/2024", "Storekeeper", "أمين مخزن", "+97455550000",
            "ramesh@example.qa", "28435612345", "", "N1234567", "QA54QNBA000000000000693123456", "QNBA", "Y", "None", "3000", "1000", "500",
            "", "", "N", "Y", "Unlimited", "0", "15/12/2026", "20/04/2030", "01/03/2027", "01/03/2027"
        ];
        for (var i = 0; i < sample.Length; i++) sheet.Cell(3, i + 1).Value = sample[i];
        sheet.Row(2).Height = 32;
        sheet.Columns().AdjustToContents(1, 3, 12, 40);
        sheet.SheetView.FreezeRows(2);

        var lists = workbook.Worksheets.Add("Lists");
        lists.Cell(1, 1).Value = "Y/N";
        lists.Cell(2, 1).Value = "Y";
        lists.Cell(3, 1).Value = "N";
        lists.Cell(1, 2).Value = "Gender";
        lists.Cell(2, 2).Value = "M";
        lists.Cell(3, 2).Value = "F";
        lists.Cell(1, 3).Value = "SocialInsurance";
        lists.Cell(2, 3).Value = "None";
        lists.Cell(3, 3).Value = "Grsia";
        lists.Cell(4, 3).Value = "GccHome";
        lists.Cell(1, 4).Value = "ContractType";
        lists.Cell(2, 4).Value = "Unlimited";
        lists.Cell(3, 4).Value = "Limited";
        int Col(string key) => Array.FindIndex(Columns, c => c.Key == key) + 1;
        void ListValidation(string key, string range)
        {
            var validation = sheet.Range(FirstDataRow, Col(key), MaxRows + FirstDataRow, Col(key)).CreateDataValidation();
            validation.List(lists.Range(range), true);
        }
        ListValidation("Gender", "B2:B3");
        foreach (var key in new[] { "InWps", "AccommodationProvided", "FoodProvided" }) ListValidation(key, "A2:A3");
        ListValidation("SocialInsurance", "C2:C4");
        ListValidation("ContractType", "D2:D3");

        var help = workbook.Worksheets.Add("Instructions");
        string[] lines =
        [
            "How to fill this template",
            "1. Fill one employee per row in the Employees sheet, starting at row 3 (replace the sample row).",
            "2. Dark green columns are required. Dates as dd/MM/yyyy. Amounts are monthly QAR.",
            "3. Housing is required when accommodation is not provided (min QAR 500); food likewise (min QAR 300). Basic ≥ QAR 1,000.",
            "4. Upload the file and press Validate: nothing is saved until every row is valid and you press Import.",
            "",
            "طريقة تعبئة القالب",
            "١. أدخل موظفاً واحداً في كل صف في ورقة Employees ابتداءً من الصف الثالث (استبدل المثال).",
            "٢. الأعمدة الخضراء الداكنة إلزامية. التواريخ بصيغة يوم/شهر/سنة. المبالغ شهرية بالريال القطري.",
            "٣. بدل السكن إلزامي إذا لم يوفر صاحب العمل السكن (500 ريال على الأقل)، وكذلك بدل الطعام (300 ريال). الراتب الأساسي 1000 ريال على الأقل.",
            "٤. ارفع الملف واضغط تحقق: لن يتم حفظ أي بيانات حتى تصبح جميع الصفوف صحيحة وتضغط استيراد."
        ];
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Cell(7, 1).Style.Font.Bold = true;
        help.Column(1).Width = 130;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}

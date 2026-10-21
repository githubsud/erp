using System.Globalization;
using ClosedXML.Excel;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Import;

/// <summary>A template column: <see cref="Key"/> is the header the file must have; <see cref="Choices"/> become a drop-down.</summary>
public sealed record ImportColumn(string Key, string En, string Ar, bool Required = false, string[]? Choices = null);

public sealed record ImportLineResult(int Row, string? Code, string? Name, bool Ok, bool Updated, IReadOnlyList<string> Errors);

public sealed record ImportSummary(bool DryRun, bool Committed, int TotalRows, int ValidRows, int Created, int Updated,
    IReadOnlyList<ImportLineResult> Rows, IReadOnlyList<string> Errors);

/// <summary>One data row of the sheet, read as text, with typed accessors that collect errors instead of throwing.</summary>
public sealed class ImportRow(int number, Dictionary<string, string?> values)
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy"];

    public int RowNumber { get; } = number;
    public List<string> Errors { get; } = [];

    public string? Text(string key) => values.GetValueOrDefault(key) is { Length: > 0 } v ? v.Trim() : null;

    public decimal? Number(string key, int decimals = 4)
    {
        var text = Text(key);
        if (text is null) return null;
        if (decimal.TryParse(text.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        Errors.Add($"{key}: '{text}' is not a number / ليس رقماً");
        return null;
    }

    public DateOnly? Date(string key)
    {
        var text = Text(key);
        if (text is null) return null;
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        Errors.Add($"{key}: '{text}' is not a date (dd/MM/yyyy) / ليس تاريخاً");
        return null;
    }

    public bool YesNo(string key, bool defaultValue) =>
        Text(key)?.ToUpperInvariant() switch
        {
            "Y" or "YES" or "TRUE" or "1" or "نعم" => true,
            "N" or "NO" or "FALSE" or "0" or "لا" => false,
            _ => defaultValue
        };
}

/// <summary>
/// Onboarding from Excel, shared by the master-data imports (items, customers, suppliers…): a bilingual template with
/// drop-downs and instructions, reading by header name, and an all-or-nothing run. Every row goes through the same services
/// as the screens inside its own savepoint, so all errors are reported at once, and nothing is kept unless every row is valid
/// and it is not a dry run.
/// </summary>
public static class ExcelImport
{
    public const int MaxRows = 5000;
    public const int MaxFileBytes = 10 * 1024 * 1024;
    private const int FirstDataRow = 3;
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static List<ImportRow> Read(Stream file, ImportColumn[] columns)
    {
        try
        {
            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);
            var map = new Dictionary<int, string>();
            foreach (var cell in sheet.Row(1).CellsUsed())
            {
                var key = columns.FirstOrDefault(c => string.Equals(c.Key, cell.GetString().Trim(), StringComparison.OrdinalIgnoreCase))?.Key;
                if (key is not null) map[cell.Address.ColumnNumber] = key;
            }
            var first = columns[0].Key;
            if (!map.ContainsValue(first))
                throw new BusinessRuleException("import.template", $"The first row must contain the template column names ({first}, …). Download the template.",
                    $"يجب أن يحتوي الصف الأول على أسماء أعمدة القالب ({first}، …). نزّل القالب.");

            var rows = new List<ImportRow>();
            var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
            for (var r = FirstDataRow; r <= last; r++)
            {
                var values = new Dictionary<string, string?>();
                foreach (var (col, key) in map)
                {
                    var cell = sheet.Cell(r, col);
                    values[key] = cell.IsEmpty() ? null
                        : cell.DataType == XLDataType.DateTime ? DateOnly.FromDateTime(cell.GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : cell.DataType == XLDataType.Number ? cell.GetDouble().ToString(CultureInfo.InvariantCulture)
                        : cell.GetString().Trim();
                }
                if (values.Values.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(new ImportRow(r, values));
            }
            if (rows.Count == 0) throw new BusinessRuleException("import.empty", "The file has no rows to import.", "لا يحتوي الملف على صفوف للاستيراد.");
            if (rows.Count > MaxRows)
                throw new BusinessRuleException("import.too_many", $"At most {MaxRows} rows can be imported at once.", $"الحد الأقصى {MaxRows} صف في كل مرة.");
            return rows;
        }
        catch (Exception ex) when (ex is not ErpException)
        {
            throw new BusinessRuleException("import.unreadable", "The file could not be read. Use the Excel template (.xlsx).",
                "تعذر قراءة الملف. استخدم قالب إكسل (.xlsx).");
        }
    }

    /// <summary>
    /// Runs <paramref name="importRow"/> for each row (returns true when it updated an existing record), then <paramref name="finish"/>
    /// (for work done once for the whole file, such as one opening-stock document) when every row was valid.
    /// </summary>
    public static async Task<ImportSummary> RunAsync(ErpDbContext db, ImportColumn[] columns, List<ImportRow> rows, bool dryRun,
        Func<ImportRow, CancellationToken, Task<bool>> importRow, Func<CancellationToken, Task>? finish, CancellationToken ct)
    {
        var results = new List<ImportLineResult>();
        var general = new List<string>();
        var codeKey = columns[0].Key;
        var nameKey = columns.FirstOrDefault(c => c.Key.StartsWith("Name", StringComparison.Ordinal))?.Key;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            var updated = false;
            foreach (var c in columns.Where(c => c.Required && row.Text(c.Key) is null))
                row.Errors.Add($"{c.En} is required / {c.Ar} مطلوب");
            if (row.Errors.Count == 0)
            {
                await transaction.CreateSavepointAsync("import_row", ct);
                try
                {
                    updated = await importRow(row, ct);
                    await db.SaveChangesAsync(ct);
                }
                catch (ErpException ex)
                {
                    row.Errors.Add(ex.Message);
                }
                catch (DbUpdateException ex)
                {
                    row.Errors.Add(PostgresErrors.Find(ex)?.MessageText ?? ex.Message);
                }
                if (row.Errors.Count > 0)
                {
                    await transaction.RollbackToSavepointAsync("import_row", ct);
                    db.ChangeTracker.Clear();
                }
                else await transaction.ReleaseSavepointAsync("import_row", ct);
            }
            results.Add(new ImportLineResult(row.RowNumber, row.Text(codeKey), nameKey is null ? null : row.Text(nameKey) ?? row.Text("NameAr"),
                row.Errors.Count == 0, updated, row.Errors.ToList()));
        }

        var allValid = results.All(r => r.Ok);
        if (allValid && finish is not null)
        {
            try
            {
                await finish(ct);
                await db.SaveChangesAsync(ct);
            }
            catch (ErpException ex)
            {
                general.Add(ex.Message);
            }
        }
        var commit = !dryRun && allValid && general.Count == 0;
        if (commit) await transaction.CommitAsync(ct);
        else await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return new ImportSummary(dryRun, commit, results.Count, results.Count(r => r.Ok), results.Count(r => r.Ok && !r.Updated),
            results.Count(r => r.Ok && r.Updated), results, general);
    }

    /// <summary>Template: column keys (required ones dark), bilingual hints, sample rows, drop-downs and an instructions sheet.</summary>
    public static byte[] Template(string sheetName, ImportColumn[] columns, string[][] samples, string[] instructionsEn, string[] instructionsAr)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);
        var lists = workbook.Worksheets.Add("Lists");
        var listCol = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            var c = columns[i];
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
            if (c.Choices is { Length: > 0 } choices)
            {
                listCol++;
                lists.Cell(1, listCol).Value = c.Key;
                for (var j = 0; j < choices.Length; j++) lists.Cell(j + 2, listCol).Value = choices[j];
                sheet.Range(FirstDataRow, i + 1, MaxRows + FirstDataRow, i + 1).CreateDataValidation()
                    .List(lists.Range(2, listCol, choices.Length + 1, listCol), true);
            }
        }
        for (var r = 0; r < samples.Length; r++)
            for (var i = 0; i < samples[r].Length && i < columns.Length; i++)
                sheet.Cell(FirstDataRow + r, i + 1).Value = samples[r][i];
        sheet.Row(2).Height = 32;
        sheet.Columns().AdjustToContents(1, FirstDataRow + samples.Length, 12, 40);
        sheet.SheetView.FreezeRows(2);
        if (listCol == 0) lists.Delete();

        var help = workbook.Worksheets.Add("Instructions");
        var lines = new List<string> { "How to fill this template" };
        lines.AddRange(instructionsEn);
        lines.Add("Dark green columns are required. Rows start at row 3 (replace the samples). Upload the file and press Check: nothing is saved until every row is valid and you press Import.");
        lines.Add("");
        lines.Add("طريقة تعبئة القالب");
        lines.AddRange(instructionsAr);
        lines.Add("الأعمدة الخضراء الداكنة إلزامية. تبدأ البيانات من الصف الثالث (استبدل الأمثلة). ارفع الملف واضغط تحقق: لن يُحفظ شيء حتى تصبح كل الصفوف صحيحة وتضغط استيراد.");
        for (var i = 0; i < lines.Count; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Cell(instructionsEn.Length + 4, 1).Style.Font.Bold = true;
        help.Column(1).Width = 130;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}

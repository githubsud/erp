using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Erp.SharedKernel.Domain;

namespace Erp.Modules.Finance.Banking;

public sealed record ParsedBankLine(int Row, DateOnly Date, string? Description, string? Reference, decimal Amount, decimal? Balance);

public sealed record ParsedStatement(IReadOnlyList<ParsedBankLine> Lines, IReadOnlyList<string> Errors);

/// <summary>
/// Reads bank statement exports (.csv or .xlsx). The header row is found automatically (banks often put a title block above
/// it) and columns are recognised by common English and Arabic names: a signed <c>Amount</c> column, or <c>Debit</c>
/// (money out) and <c>Credit</c> (money in) columns. Amounts may use thousands separators, brackets or CR/DR suffixes.
/// </summary>
public static class BankStatementParser
{
    private static readonly string[] DateNames = ["date", "txn date", "transaction date", "value date", "posting date", "trans date", "التاريخ", "تاريخ العملية", "تاريخ القيد"];
    private static readonly string[] DescriptionNames = ["description", "narration", "details", "particulars", "remarks", "transaction details", "البيان", "الوصف", "التفاصيل"];
    private static readonly string[] ReferenceNames = ["reference", "ref", "ref no", "reference no", "cheque no", "cheque", "chq no", "document no", "المرجع", "رقم الشيك", "رقم المرجع"];
    private static readonly string[] AmountNames = ["amount", "المبلغ"];
    private static readonly string[] DebitNames = ["debit", "withdrawal", "withdrawals", "dr", "debit amount", "مدين", "سحب", "مسحوبات"];
    private static readonly string[] CreditNames = ["credit", "deposit", "deposits", "cr", "credit amount", "دائن", "إيداع", "ايداع", "إيداعات"];
    private static readonly string[] BalanceNames = ["balance", "running balance", "closing balance", "الرصيد"];

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "dd/MM/yy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy",
        "d MMM yyyy", "dd-MMM-yy", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm"
    ];

    public static ParsedStatement Read(Stream file, string fileName) =>
        Parse(fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(file)
            : ReadXlsx(file));

    public static ParsedStatement Parse(IReadOnlyList<string?[]> table)
    {
        var headerRow = -1;
        Dictionary<string, int> columns = [];
        for (var r = 0; r < Math.Min(table.Count, 30) && headerRow < 0; r++)
        {
            var found = MapColumns(table[r]);
            if (found.ContainsKey("date") && (found.ContainsKey("amount") || found.ContainsKey("debit") || found.ContainsKey("credit")))
            {
                headerRow = r;
                columns = found;
            }
        }
        if (headerRow < 0)
            throw new BusinessRuleException("bank.no_header",
                "No header row was found. The file needs a Date column and either an Amount column or Debit and Credit columns.",
                "لم يتم العثور على صف العناوين. يجب أن يحتوي الملف على عمود التاريخ وعمود المبلغ أو عمودي المدين والدائن.");

        var lines = new List<ParsedBankLine>();
        var errors = new List<string>();
        for (var r = headerRow + 1; r < table.Count; r++)
        {
            var row = table[r];
            string? Cell(string key) => columns.TryGetValue(key, out var c) && c < row.Length && !string.IsNullOrWhiteSpace(row[c]) ? row[c]!.Trim() : null;
            // Rows without a date are totals, footers or blank lines.
            var dateText = Cell("date");
            if (dateText is null) continue;
            if (!TryDate(dateText, out var date))
            {
                errors.Add($"Row {r + 1}: unreadable date '{dateText}' / تاريخ غير مقروء");
                continue;
            }
            decimal amount;
            if (columns.ContainsKey("amount") && Cell("amount") is { } amountText)
            {
                if (!TryAmount(amountText, out amount)) { errors.Add($"Row {r + 1}: unreadable amount '{amountText}' / مبلغ غير مقروء"); continue; }
            }
            else
            {
                var debitOk = TryAmount(Cell("debit"), out var debit);
                var creditOk = TryAmount(Cell("credit"), out var credit);
                if (!debitOk || !creditOk) { errors.Add($"Row {r + 1}: unreadable debit/credit / مبلغ غير مقروء"); continue; }
                amount = Math.Abs(credit) - Math.Abs(debit);
            }
            if (amount == 0) continue;
            decimal? balance = TryAmount(Cell("balance"), out var b) && Cell("balance") is not null ? b : null;
            lines.Add(new ParsedBankLine(r + 1, date, Truncate(Cell("description"), 500), Truncate(Cell("reference"), 100), Money.Round(amount), balance));
        }
        return new ParsedStatement(lines, errors);
    }

    /// <summary>A CSV with the standard column names, for reshaping exports from any bank.</summary>
    public static byte[] Template() =>
    [
        .. Encoding.UTF8.GetPreamble(),
        .. Encoding.UTF8.GetBytes("Date,Description,Reference,Debit,Credit,Balance\n01/10/2026,Opening balance,,,,100000.00\n" +
                                  "02/10/2026,CHQ 000123 Doha Building Materials,000123,1092.00,,98908.00\n" +
                                  "03/10/2026,Transfer from Al Wakra Contracting,TT-77812,,5000.00,103908.00\n")
    ];

    private static Dictionary<string, int> MapColumns(string?[] header)
    {
        var map = new Dictionary<string, int>();
        for (var c = 0; c < header.Length; c++)
        {
            var name = header[c]?.Trim().Trim('"').ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var (key, names) in new[] { ("date", DateNames), ("description", DescriptionNames), ("reference", ReferenceNames),
                         ("amount", AmountNames), ("debit", DebitNames), ("credit", CreditNames), ("balance", BalanceNames) })
                if (!map.ContainsKey(key) && names.Contains(name)) map[key] = c;
        }
        return map;
    }

    internal static bool TryDate(string text, out DateOnly date)
    {
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        // Excel serial dates exported as numbers.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 30000 and < 80000)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        return false;
    }

    /// <summary>Empty is zero. Accepts 1,234.50 · (1,234.50) · -1234.5 · 1,234.50 CR / DR · QAR 1,234.50.</summary>
    internal static bool TryAmount(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim().ToUpperInvariant().Replace("QAR", "").Replace("QR", "").Replace(",", "").Replace(" ", "");
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.EndsWith("DR")) { negative = true; t = t[..^2]; }
        else if (t.EndsWith("CR")) t = t[..^2];
        if (t.EndsWith('-')) { negative = true; t = t[..^1]; }
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)) return false;
        if (negative) amount = -Math.Abs(amount);
        return true;
    }

    private static string? Truncate(string? text, int max) => text is { Length: > 0 } t && t.Length > max ? t[..max] : text;

    private static List<string?[]> ReadXlsx(Stream file)
    {
        using var workbook = Erp.Infrastructure.Import.ExcelImport.Open(file);
        var sheet = workbook.Worksheet(1);
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var table = new List<string?[]>();
        for (var r = 1; r <= lastRow; r++)
        {
            var values = new string?[lastCol];
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = sheet.Cell(r, c);
                values[c - 1] = cell.IsEmpty() ? null
                    : cell.DataType == XLDataType.DateTime ? cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : cell.DataType == XLDataType.Number ? cell.GetDouble().ToString(CultureInfo.InvariantCulture)
                    : cell.GetString();
            }
            table.Add(values);
        }
        return table;
    }

    private static List<string?[]> ReadCsv(Stream file)
    {
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        var sample = string.Join('\n', lines.Take(10));
        var separator = sample.Contains('\t') ? '\t' : sample.Count(ch => ch == ';') > sample.Count(ch => ch == ',') ? ';' : ',';
        return lines.Select(l => SplitCsv(l, separator)).ToList();
    }

    /// <summary>Splits one CSV line, honouring quoted fields (amounts such as "1,234.50" are quoted by most banks).</summary>
    internal static string?[] SplitCsv(string line, char separator)
    {
        var fields = new List<string?>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == separator && !quoted)
            {
                fields.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString().Trim());
        return fields.ToArray();
    }
}

using System.Globalization;
using System.Text;

namespace Erp.SharedKernel.Application;

/// <summary>A rendered report ready to attach to an email.</summary>
public sealed record ReportFile(string FileName, string ContentType, byte[] Content, string SummaryEn, string SummaryAr);

/// <summary>
/// A report that can be emailed on a schedule. Each module registers its own; Core runs the schedules. Rendering happens for the
/// tenant and company already set on <see cref="ITenantContext"/>; the schedule owner's permission is checked before every send.
/// </summary>
public interface IScheduledReport
{
    string Key { get; }
    string NameEn { get; }
    string NameAr { get; }

    /// <summary>The permission the schedule owner needs to receive this report.</summary>
    string Permission { get; }

    Task<ReportFile> RenderAsync(CancellationToken ct);
}

/// <summary>CSV that Excel opens correctly with Arabic text (UTF-8 with BOM, invariant numbers).</summary>
public static class CsvFile
{
    public const string ContentType = "text/csv";

    public static byte[] Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Format)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("0.00##", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => Escape(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => Escape(value.ToString() ?? "")
    };

    public static string Escape(string value)
    {
        // Guard against formula injection when the file is opened in a spreadsheet.
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

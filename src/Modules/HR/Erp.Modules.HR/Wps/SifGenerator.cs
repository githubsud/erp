using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Erp.Modules.HR.Wps;

public sealed record SifHeader(
    string EmployerEid,
    string PayerEid,
    string? PayerQid,
    string PayerBankShortName,
    string PayerIban,
    int Year,
    int Month,
    DateTimeOffset CreatedAtQatar);

public sealed record SifRecord(
    string? Qid,
    string? VisaId,
    string Name,
    string BankShortName,
    string Iban,
    decimal WorkingDays,
    decimal NetSalary,
    decimal BasicSalary,
    decimal ExtraHours,
    decimal ExtraIncome,
    decimal Deductions,
    decimal HousingAllowance,
    decimal FoodAllowance,
    decimal TransportAllowance,
    decimal OvertimeAllowance,
    string? Notes);

public sealed record SifFile(string FileName, string Content, string Sha256, decimal TotalSalaries, int RecordCount);

public sealed record SifValidationError(int? Record, string Field, string Message);

/// <summary>
/// Builds the WPS Salary Information File (SIF): CSV per RFC 4180 with a fixed field order, as described in the
/// Qatar Central Bank WPS file format. Version 0 carries the original fields; version 1 adds the allowance breakdown.
/// ⚠️ The exact field list, file name pattern and accepted versions must be validated against each payer bank's
/// current specification and validator before go-live; the layout is isolated here so a change is local.
/// </summary>
public static class SifGenerator
{
    private static readonly string[] HeaderFields =
    [
        "Employer EID", "File Creation Date", "File Creation Time", "Payer EID", "Payer QID", "Payer Bank Short Name",
        "Payer IBAN", "Salary Year and Month", "Total Salaries", "Total records", "SIF Version"
    ];

    private static readonly string[] RecordFieldsV0 =
    [
        "Record Sequence", "Employee QID", "Employee Visa ID", "Employee Name", "Employee Bank Short Name", "Employee Account",
        "Salary Frequency", "Number of Working days", "Net Salary", "Basic Salary", "Extra hours", "Extra income", "Deductions",
        "Payment Type", "Notes / Comments"
    ];

    private static readonly string[] RecordFieldsV1Extra =
    [
        "Housing Allowance", "Food Allowance", "Transportation Allowance", "Over Time Allowance", "Deduction Reason Code",
        "Extra Field 1", "Extra Field 2"
    ];

    public static IReadOnlyList<SifValidationError> Validate(SifHeader header, IReadOnlyList<SifRecord> records)
    {
        var errors = new List<SifValidationError>();
        if (string.IsNullOrWhiteSpace(header.EmployerEid)) errors.Add(new(null, "Employer EID", "Employer establishment ID is not set in HR settings."));
        if (string.IsNullOrWhiteSpace(header.PayerEid)) errors.Add(new(null, "Payer EID", "Payer EID is not set in HR settings."));
        if (!QatarIdentifiers.IsValidBankShortName(header.PayerBankShortName)) errors.Add(new(null, "Payer Bank Short Name", "Payer bank short name must be 4 letters."));
        if (!QatarIdentifiers.IsValidQatarIban(header.PayerIban)) errors.Add(new(null, "Payer IBAN", "Payer IBAN is not a valid Qatari IBAN."));
        if (records.Count == 0) errors.Add(new(null, "Records", "There are no employees to pay through WPS."));

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var seq = i + 1;
            if (!QatarIdentifiers.IsValidQid(r.Qid) && string.IsNullOrWhiteSpace(r.VisaId))
                errors.Add(new(seq, "Employee QID", $"{r.Name}: a valid 11-digit QID (or a visa ID) is required."));
            if (!QatarIdentifiers.IsValidBankShortName(r.BankShortName))
                errors.Add(new(seq, "Employee Bank Short Name", $"{r.Name}: bank short name must be 4 letters."));
            if (!QatarIdentifiers.IsValidQatarIban(r.Iban))
                errors.Add(new(seq, "Employee Account", $"{r.Name}: IBAN is missing or invalid."));
            if (r.NetSalary <= 0)
                errors.Add(new(seq, "Net Salary", $"{r.Name}: net salary must be greater than zero."));
            var computed = r.BasicSalary + r.ExtraIncome + r.HousingAllowance + r.FoodAllowance + r.TransportAllowance + r.OvertimeAllowance - r.Deductions;
            if (computed != r.NetSalary)
                errors.Add(new(seq, "Net Salary", $"{r.Name}: net salary {r.NetSalary:0.00} does not equal its components ({computed:0.00})."));
        }
        return errors;
    }

    public static SifFile Generate(SifHeader header, IReadOnlyList<SifRecord> records, string version)
    {
        var v1 = version != "0";
        var total = records.Sum(r => r.NetSalary);
        var sb = new StringBuilder();

        var headerFields = v1 ? HeaderFields : HeaderFields[..^1];
        AppendRow(sb, headerFields);
        var headerValues = new List<string>
        {
            header.EmployerEid, header.CreatedAtQatar.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            header.CreatedAtQatar.ToString("HHmm", CultureInfo.InvariantCulture), header.PayerEid, header.PayerQid ?? "",
            header.PayerBankShortName.ToUpperInvariant(), QatarIdentifiers.Normalize(header.PayerIban),
            $"{header.Year:D4}{header.Month:D2}", Amount(total), records.Count.ToString(CultureInfo.InvariantCulture)
        };
        if (v1) headerValues.Add(version);
        AppendRow(sb, headerValues);

        AppendRow(sb, v1 ? [.. RecordFieldsV0, .. RecordFieldsV1Extra] : RecordFieldsV0);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var values = new List<string>
            {
                (i + 1).ToString(CultureInfo.InvariantCulture),
                r.Qid is null ? "" : QatarIdentifiers.Normalize(r.Qid),
                r.VisaId ?? "",
                SanitizeName(r.Name),
                r.BankShortName.ToUpperInvariant(),
                QatarIdentifiers.Normalize(r.Iban),
                "M",
                r.WorkingDays.ToString("0.##", CultureInfo.InvariantCulture),
                Amount(r.NetSalary),
                Amount(r.BasicSalary),
                r.ExtraHours.ToString("0.##", CultureInfo.InvariantCulture),
                Amount(v1 ? r.ExtraIncome : r.ExtraIncome + r.HousingAllowance + r.FoodAllowance + r.TransportAllowance + r.OvertimeAllowance),
                Amount(r.Deductions),
                "Normal Payment",
                r.Notes ?? ""
            };
            if (v1)
                values.AddRange([Amount(r.HousingAllowance), Amount(r.FoodAllowance), Amount(r.TransportAllowance), Amount(r.OvertimeAllowance), "", "", ""]);
            AppendRow(sb, values);
        }

        var content = sb.ToString();
        var fileName = $"SIF_{header.EmployerEid}_{header.PayerBankShortName.ToUpperInvariant()}_{header.CreatedAtQatar:yyyyMMdd}_{header.CreatedAtQatar:HHmm}.csv";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new SifFile(fileName, content, sha, total, records.Count);
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Banks expect Latin names without delimiters.</summary>
    private static string SanitizeName(string name) =>
        string.Join(' ', name.Replace(",", " ").Replace("\"", "").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static void AppendRow(StringBuilder sb, IEnumerable<string> values)
    {
        sb.Append(string.Join(',', values.Select(Escape)));
        sb.Append("\r\n");
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}

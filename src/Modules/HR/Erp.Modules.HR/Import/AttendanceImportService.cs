using System.Globalization;
using ClosedXML.Excel;
using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Import;

public sealed record AttendanceSummary(Guid EmployeeId, string EmpNo, string NameEn, string NameAr, int Days, decimal Worked,
    decimal Normal, decimal Night, decimal RestDay, decimal Holiday);

public sealed record AttendanceImportResult(bool DryRun, bool Committed, int TotalRows, int ValidRows, IReadOnlyList<ImportRowResult> Rows,
    IReadOnlyList<AttendanceSummary> Employees);

/// <summary>
/// Attendance from biometric devices (.xlsx or .csv), in either layout:
/// one row per day (<c>EmpNo, Date, In, Out[, Holiday]</c>) or raw punches (<c>EmpNo, Punch</c>), where the first and
/// last punch of a day become in and out. A valid file replaces those days and rebuilds the employees' attendance
/// overtime inputs for the affected months. All-or-nothing, like the employee import.
/// </summary>
internal sealed class AttendanceImportService(ErpDbContext db, ITenantContext tenant, HrSettingsProvider settings)
{
    public const int MaxRows = 50_000;

    private sealed record Day(int Row, string EmpNo, DateOnly Date, TimeOnly In, TimeOnly Out, bool Holiday);

    public async Task<AttendanceImportResult> ImportAsync(Stream file, string fileName, bool dryRun, CancellationToken ct)
    {
        List<Dictionary<string, string?>> raw;
        try
        {
            raw = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(file) : ReadXlsx(file);
        }
        catch (Exception ex) when (ex is not BusinessRuleException)
        {
            throw new BusinessRuleException("import.unreadable", "The file could not be read. Use .xlsx or .csv.", "تعذر قراءة الملف. استخدم ملف xlsx أو csv.");
        }
        if (raw.Count == 0)
            throw new BusinessRuleException("import.empty", "The file has no attendance rows.", "لا يحتوي الملف على بيانات حضور.");
        if (raw.Count > MaxRows)
            throw new BusinessRuleException("import.too_many", $"At most {MaxRows} rows can be imported at once.", $"الحد الأقصى {MaxRows} صف في كل مرة.");

        var results = new List<ImportRowResult>();
        var days = raw[0].ContainsKey("Punch") ? FromPunches(raw, results) : FromDays(raw, results);

        var companyId = tenant.RequireCompanyId();
        var hoursPerDay = (await settings.GetAsync(ct)).HoursPerDay;
        var empNos = days.Select(d => d.EmpNo).Distinct().ToList();
        var employees = (await db.Set<Employee>().AsNoTracking().Where(e => e.CompanyId == companyId && empNos.Contains(e.EmpNo)).ToListAsync(ct))
            .ToDictionary(e => e.EmpNo, StringComparer.OrdinalIgnoreCase);
        var lockedMonths = (await db.Set<PayrollRun>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && (r.Status == PayrollStatus.Approved || r.Status == PayrollStatus.Posted || r.Status == PayrollStatus.Paid))
                .Select(r => new { r.Year, r.Month }).ToListAsync(ct))
            .Select(r => (r.Year, r.Month)).ToHashSet();

        var valid = new List<(Day Day, Employee Employee, AttendanceHours Hours)>();
        var seen = new HashSet<(string, DateOnly)>();
        foreach (var d in days)
        {
            var errors = new List<string>();
            if (!employees.TryGetValue(d.EmpNo, out var employee)) errors.Add($"Unknown employee {d.EmpNo} / موظف غير معروف");
            if (!seen.Add((d.EmpNo.ToUpperInvariant(), d.Date))) errors.Add($"{d.Date:dd/MM/yyyy} appears twice / التاريخ مكرر");
            if (lockedMonths.Contains((d.Date.Year, d.Date.Month)))
                errors.Add($"Payroll for {d.Date:yyyy-MM} is already approved / تم اعتماد رواتب الشهر");
            if (employee is not null && (d.Date < employee.JoiningDate || employee.Status == EmployeeStatus.Terminated))
                errors.Add("Employee was not in service on this date / الموظف لم يكن على رأس العمل");
            var hours = AttendanceCalculator.Calculate(d.Date, d.In, d.Out, hoursPerDay, d.Holiday);
            if (hours.Worked is <= 0 or > AttendanceCalculator.MaxShiftHours)
                errors.Add($"{hours.Worked} hours worked — check for a missing punch / تحقق من البصمة");

            results.Add(new ImportRowResult(d.Row, d.EmpNo, employee?.NameEn ?? employee?.NameAr, errors.Count == 0, errors));
            if (errors.Count == 0) valid.Add((d, employee!, hours));
        }
        results.Sort((a, b) => a.Row.CompareTo(b.Row));

        var summary = valid.GroupBy(v => v.Employee.Id)
            .Select(g => new AttendanceSummary(g.Key, g.First().Employee.EmpNo, g.First().Employee.NameEn ?? "", g.First().Employee.NameAr ?? "",
                g.Count(), g.Sum(x => x.Hours.Worked), g.Sum(x => x.Hours.Normal), g.Sum(x => x.Hours.Night), g.Sum(x => x.Hours.RestDay),
                g.Sum(x => x.Hours.Holiday)))
            .OrderBy(s => s.EmpNo).ToList();

        var okCount = results.Count(r => r.Ok);
        var commit = !dryRun && okCount == results.Count;
        if (commit)
        {
            await db.InTransactionAsync(async token =>
            {
                var ids = valid.Select(v => v.Employee.Id).Distinct().ToList();
                var first = valid.Min(v => v.Day.Date);
                var last = valid.Max(v => v.Day.Date);
                var existing = await db.Set<AttendanceDay>().Where(a => ids.Contains(a.EmployeeId) && a.WorkDate >= first && a.WorkDate <= last).ToListAsync(token);
                var replaced = valid.Select(v => (v.Employee.Id, v.Day.Date)).ToHashSet();
                db.RemoveRange(existing.Where(a => replaced.Contains((a.EmployeeId, a.WorkDate))));
                await db.SaveChangesAsync(token);

                foreach (var (day, employee, hours) in valid)
                    db.Add(new AttendanceDay
                    {
                        EmployeeId = employee.Id, WorkDate = day.Date, TimeIn = day.In, TimeOut = day.Out, IsHoliday = day.Holiday,
                        WorkedHours = hours.Worked, NormalOvertime = hours.Normal, NightOvertime = hours.Night,
                        RestDayOvertime = hours.RestDay, HolidayOvertime = hours.Holiday
                    });
                await db.SaveChangesAsync(token);

                foreach (var month in valid.Select(v => (v.Day.Date.Year, v.Day.Date.Month)).Distinct())
                    await RebuildOvertimeInputsAsync(companyId, ids, month.Year, month.Month, token);
                return 0;
            }, ct);
        }
        return new AttendanceImportResult(dryRun, commit, results.Count, okCount, results, summary);
    }

    /// <summary>Replaces the attendance-generated overtime inputs of these employees for the month with the month's totals.</summary>
    private async Task RebuildOvertimeInputsAsync(Guid companyId, IReadOnlyCollection<Guid> employeeIds, int year, int month, CancellationToken ct)
    {
        var overtime = await db.Set<PayComponent>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.WpsField == WpsField.Overtime && c.IsActive, ct)
            ?? throw new BusinessRuleException("hr.overtime_component", "No active overtime pay component.", "لا يوجد مكون نشط للعمل الإضافي.");
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);

        db.RemoveRange(await db.Set<PayrollInput>()
            .Where(i => employeeIds.Contains(i.EmployeeId) && i.Year == year && i.Month == month && i.Source == InputSource.Attendance).ToListAsync(ct));

        var totals = await db.Set<AttendanceDay>().AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.WorkDate >= start && a.WorkDate <= end)
            .GroupBy(a => a.EmployeeId)
            .Select(g => new
            {
                EmployeeId = g.Key, Days = g.Count(), Normal = g.Sum(a => a.NormalOvertime), Night = g.Sum(a => a.NightOvertime),
                RestDay = g.Sum(a => a.RestDayOvertime), Holiday = g.Sum(a => a.HolidayOvertime)
            }).ToListAsync(ct);

        foreach (var t in totals)
            foreach (var (type, hours) in new[] { (OvertimeType.Normal, t.Normal), (OvertimeType.Night, t.Night), (OvertimeType.RestDay, t.RestDay), (OvertimeType.Holiday, t.Holiday) })
                if (hours > 0)
                    db.Add(new PayrollInput
                    {
                        EmployeeId = t.EmployeeId, Year = year, Month = month, PayComponentId = overtime.Id, OvertimeType = type, Hours = hours,
                        Source = InputSource.Attendance, Notes = $"Attendance: {t.Days} day(s) / الحضور: {t.Days} يوم"
                    });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AttendanceSummary>> MonthSummaryAsync(int year, int month, CancellationToken ct)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var companyId = tenant.RequireCompanyId();
        var rows = await (from a in db.Set<AttendanceDay>().AsNoTracking()
                          join e in db.Set<Employee>() on a.EmployeeId equals e.Id
                          where e.CompanyId == companyId && a.WorkDate >= start && a.WorkDate <= end
                          group a by new { e.Id, e.EmpNo, e.NameEn, e.NameAr } into g
                          select new
                          {
                              g.Key, Days = g.Count(), Worked = g.Sum(a => a.WorkedHours), Normal = g.Sum(a => a.NormalOvertime),
                              Night = g.Sum(a => a.NightOvertime), RestDay = g.Sum(a => a.RestDayOvertime), Holiday = g.Sum(a => a.HolidayOvertime)
                          }).ToListAsync(ct);
        return rows.Select(r => new AttendanceSummary(r.Key.Id, r.Key.EmpNo, r.Key.NameEn ?? "", r.Key.NameAr ?? "", r.Days, r.Worked, r.Normal,
            r.Night, r.RestDay, r.Holiday)).OrderBy(r => r.EmpNo).ToList();
    }

    // Parsing ------------------------------------------------------------------------------------------------

    private static List<Day> FromDays(List<Dictionary<string, string?>> raw, List<ImportRowResult> results)
    {
        var days = new List<Day>();
        foreach (var row in raw)
        {
            var errors = new List<string>();
            var rowNo = int.Parse(row["__row"]!, CultureInfo.InvariantCulture);
            var empNo = Text(row, "EmpNo");
            if (empNo is null) errors.Add("EmpNo is required / الرقم الوظيفي مطلوب");
            var date = ParseDate(Text(row, "Date"), "Date", errors);
            var timeIn = ParseTime(Text(row, "In"), "In", errors);
            var timeOut = ParseTime(Text(row, "Out"), "Out", errors);
            if (errors.Count > 0) results.Add(new ImportRowResult(rowNo, empNo, null, false, errors));
            else days.Add(new Day(rowNo, empNo!, date!.Value, timeIn!.Value, timeOut!.Value, YesNo(Text(row, "Holiday"))));
        }
        return days;
    }

    private static List<Day> FromPunches(List<Dictionary<string, string?>> raw, List<ImportRowResult> results)
    {
        var punches = new List<(int Row, string EmpNo, DateTime At)>();
        foreach (var row in raw)
        {
            var rowNo = int.Parse(row["__row"]!, CultureInfo.InvariantCulture);
            var empNo = Text(row, "EmpNo");
            var text = Text(row, "Punch");
            if (empNo is null || text is null || !TryDateTime(text, out var at))
                results.Add(new ImportRowResult(rowNo, empNo, null, false, [$"Punch: '{text}' is not a date and time (dd/MM/yyyy HH:mm) / وقت البصمة غير صحيح"]));
            else punches.Add((rowNo, empNo, at));
        }

        var days = new List<Day>();
        foreach (var g in punches.GroupBy(p => (p.EmpNo.ToUpperInvariant(), DateOnly.FromDateTime(p.At))))
        {
            var ordered = g.OrderBy(p => p.At).ToList();
            if (ordered.Count < 2)
            {
                results.Add(new ImportRowResult(ordered[0].Row, ordered[0].EmpNo, null, false, ["Only one punch on this day / بصمة واحدة فقط في هذا اليوم"]));
                continue;
            }
            days.Add(new Day(ordered[0].Row, ordered[0].EmpNo, g.Key.Item2, TimeOnly.FromDateTime(ordered[0].At), TimeOnly.FromDateTime(ordered[^1].At), false));
        }
        return days;
    }

    private static readonly string[] Keys = ["EmpNo", "Date", "In", "Out", "Holiday", "Punch"];

    private static string? Key(string header) =>
        Keys.FirstOrDefault(k => string.Equals(k, header.Trim().Trim('"', '﻿'), StringComparison.OrdinalIgnoreCase));

    private static List<Dictionary<string, string?>> Normalize(List<string?[]> table)
    {
        var header = table.Count > 0 ? table[0] : [];
        var map = new Dictionary<int, string>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] is { } h && Key(h) is { } k) map[i] = k;
        if (!map.ContainsValue("EmpNo") || !(map.ContainsValue("Punch") || (map.ContainsValue("Date") && map.ContainsValue("In") && map.ContainsValue("Out"))))
            throw new BusinessRuleException("import.template", "The first row must name the columns: EmpNo, Date, In, Out (or EmpNo, Punch).",
                "يجب أن يحتوي الصف الأول على أسماء الأعمدة: EmpNo, Date, In, Out أو EmpNo, Punch.");

        var rows = new List<Dictionary<string, string?>>();
        for (var r = 1; r < table.Count; r++)
        {
            var values = new Dictionary<string, string?> { ["__row"] = (r + 1).ToString(CultureInfo.InvariantCulture) };
            foreach (var (col, key) in map) values[key] = col < table[r].Length ? table[r][col]?.Trim() : null;
            if (map.Values.Any(k => !string.IsNullOrWhiteSpace(values[k]))) rows.Add(values);
        }
        return rows;
    }

    private static List<Dictionary<string, string?>> ReadXlsx(Stream file)
    {
        using var workbook = new XLWorkbook(file);
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
                    : cell.DataType == XLDataType.DateTime ? cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : cell.DataType == XLDataType.TimeSpan ? cell.GetTimeSpan().ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                    : cell.DataType == XLDataType.Number ? cell.GetDouble().ToString(CultureInfo.InvariantCulture)
                    : cell.GetString();
            }
            table.Add(values);
        }
        return Normalize(table);
    }

    private static List<Dictionary<string, string?>> ReadCsv(Stream file)
    {
        using var reader = new StreamReader(file);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        var head = lines.Count > 0 ? lines[0] : "";
        var separator = head.Contains('\t') ? '\t' : head.Count(ch => ch == ';') > head.Count(ch => ch == ',') ? ';' : ',';
        return Normalize(lines.Select(l => l.Split(separator).Select(v => (string?)v.Trim().Trim('"')).ToArray()).ToList());
    }

    private static string? Text(Dictionary<string, string?> row, string key) => row.GetValueOrDefault(key) is { Length: > 0 } v ? v.Trim() : null;

    private static bool YesNo(string? text) => text?.ToUpperInvariant() is "Y" or "YES" or "TRUE" or "1" or "نعم";

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm",
        "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd/MM/yyyy hh:mm tt", "d/M/yyyy h:mm tt"
    ];

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy"];

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "hh:mm tt"];

    private static bool TryDateTime(string text, out DateTime value) =>
        DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static DateOnly? ParseDate(string? text, string key, List<string> errors)
    {
        if (text is null) { errors.Add($"{key} is required / التاريخ مطلوب"); return null; }
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (TryDateTime(text, out var dt)) return DateOnly.FromDateTime(dt);
        errors.Add($"{key}: '{text}' is not a date (use dd/MM/yyyy) / تاريخ غير صحيح");
        return null;
    }

    private static TimeOnly? ParseTime(string? text, string key, List<string> errors)
    {
        if (text is null) { errors.Add($"{key} is required / الوقت مطلوب"); return null; }
        if (TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        if (TryDateTime(text, out var dt)) return TimeOnly.FromDateTime(dt);
        // Excel time stored as a fraction of a day.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction is >= 0 and < 1)
            return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Round(fraction * 24 * 60)));
        errors.Add($"{key}: '{text}' is not a time (use HH:mm) / وقت غير صحيح");
        return null;
    }

    /// <summary>Template for the daily layout, with instructions for raw punch exports.</summary>
    public static byte[] Template()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Attendance");
        string[] header = ["EmpNo", "Date", "In", "Out", "Holiday"];
        for (var i = 0; i < header.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = header[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F5C4D");
            cell.Style.Font.FontColor = XLColor.White;
        }
        string[][] sample = [["E001", "01/03/2026", "07:00", "17:30", "N"], ["E001", "06/03/2026", "08:00", "12:00", "N"], ["E002", "01/03/2026", "20:00", "06:00", "N"]];
        for (var r = 0; r < sample.Length; r++)
            for (var c = 0; c < sample[r].Length; c++) sheet.Cell(r + 2, c + 1).Value = sample[r][c];
        sheet.Columns().AdjustToContents();

        var help = workbook.Worksheets.Add("Instructions");
        string[] lines =
        [
            "One row per employee per day: EmpNo, Date (dd/MM/yyyy), In and Out (HH:mm), Holiday (Y for public holidays).",
            "Raw device exports also work: two columns EmpNo and Punch (dd/MM/yyyy HH:mm); the first and last punch of a day are used.",
            "Hours beyond the standard day are overtime; 21:00–06:00 is night overtime; Friday and holidays are rest-day / holiday overtime.",
            "Re-importing a day replaces it. Overtime inputs for the month are rebuilt from all imported days.",
            "",
            "صف لكل موظف لكل يوم: الرقم الوظيفي، التاريخ، وقت الدخول والخروج، Holiday = Y للعطلات الرسمية.",
            "يمكن أيضاً استيراد ملف البصمات مباشرة بعمودين EmpNo و Punch؛ تُستخدم أول وآخر بصمة في اليوم.",
            "الساعات الزائدة عن يوم العمل عمل إضافي؛ من 9 مساءً إلى 6 صباحاً عمل إضافي ليلي؛ الجمعة والعطلات بأجر يوم الراحة / العطلة.",
            "إعادة استيراد يوم تستبدله، ويُعاد احتساب إدخالات العمل الإضافي للشهر من جميع الأيام المستوردة."
        ];
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Column(1).Width = 130;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}

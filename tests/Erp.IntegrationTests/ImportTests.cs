using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class ImportTests(ErpFixture fixture)
{
    public sealed record RowResult(int Row, string? EmpNo, bool Ok, IReadOnlyList<string> Errors);
    public sealed record ImportResult(bool DryRun, bool Committed, int TotalRows, int ValidRows, IReadOnlyList<RowResult> Rows);

    private static async Task<byte[]> FillTemplateAsync(HttpClient client, params string[][] rows)
    {
        var template = await client.GetByteArrayAsync("/api/hr/imports/employees/template");
        using var workbook = new XLWorkbook(new MemoryStream(template));
        var sheet = workbook.Worksheet("Employees");
        var keys = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        sheet.Row(3).Clear();
        for (var r = 0; r < rows.Length; r++)
            for (var i = 0; i < rows[r].Length; i += 2)
                sheet.Cell(3 + r, keys.IndexOf(rows[r][i]) + 1).Value = rows[r][i + 1];
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static async Task<ImportResult> UploadAsync(HttpClient client, byte[] file, bool dryRun)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(part, "file", "employees.xlsx");
        var response = await client.PostAsync($"/api/hr/imports/employees?dryRun={dryRun.ToString().ToLowerInvariant()}", content);
        return await response.ReadAsync<ImportResult>();
    }

    private static string[] Row(string empNo, string qid, string iban, string basic = "3000") =>
    [
        "EmpNo", empNo, "NameEn", $"Imported {empNo}", "NameAr", "موظف مستورد", "Nationality", "IN", "Gender", "M",
        "JoiningDate", "01/02/2025", "QID", qid, "IBAN", iban, "Basic", basic, "Housing", "1000", "FoodProvided", "Y",
        "QidExpiry", "15/12/2027", "PassportNo", "P" + qid[..7], "PassportExpiry", "01/01/2030"
    ];

    [Fact]
    public async Task Import_reports_every_problem_and_saves_nothing_until_the_whole_file_is_valid()
    {
        var t = await fixture.CreateTenantAsync();
        var goodIban = PayrollTests.Iban("QNBA", "000000000000700000001");

        var bad = await FillTemplateAsync(t.Client,
            Row("M001", "28435600001", goodIban),
            Row("M002", "28435600002", "QA00BADX000000000000000000000"),
            Row("M003", "28435600003", goodIban, basic: "800"));
        var check = await UploadAsync(t.Client, bad, dryRun: false);
        check.Committed.ShouldBeFalse();
        check.TotalRows.ShouldBe(3);
        check.ValidRows.ShouldBe(1);
        check.Rows.Single(r => r.EmpNo == "M002").Errors.ShouldContain(e => e.Contains("IBAN"));
        check.Rows.Single(r => r.EmpNo == "M003").Errors.ShouldContain(e => e.Contains("1,000"));
        (await t.Client.GetAsync<JsonElement>("/api/hr/employees?status=")).GetProperty("total").GetInt32().ShouldBe(0);

        var good = await FillTemplateAsync(t.Client,
            Row("M001", "28435600001", goodIban),
            Row("M002", "28435600002", PayrollTests.Iban("CBQA", "000000000000700000002")),
            Row("M003", "28435600003", goodIban, basic: "1200"));
        var dry = await UploadAsync(t.Client, good, dryRun: true);
        dry.ValidRows.ShouldBe(3);
        dry.Committed.ShouldBeFalse();
        (await t.Client.GetAsync<JsonElement>("/api/hr/employees?status=")).GetProperty("total").GetInt32().ShouldBe(0);

        var imported = await UploadAsync(t.Client, good, dryRun: false);
        imported.Committed.ShouldBeTrue();
        var list = await t.Client.GetAsync<JsonElement>("/api/hr/employees?status=");
        list.GetProperty("total").GetInt32().ShouldBe(3);

        var m002 = list.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("empNo").GetString() == "M002");
        m002.GetProperty("bankShortName").GetString().ShouldBe("CBQA"); // derived from the IBAN
        var id = m002.GetProperty("id").GetGuid();
        var contracts = await t.Client.GetAsync<List<JsonElement>>($"/api/hr/employees/{id}/contracts");
        contracts.Single().GetProperty("monthlyGross").GetDecimal().ShouldBe(4000m);
        var docs = await t.Client.GetAsync<List<JsonElement>>($"/api/hr/employees/{id}/documents");
        docs.Select(d => d.GetProperty("docType").GetString()).Order().ShouldBe(["Passport", "Qid"]);
    }

    [Fact]
    public async Task Import_rejects_files_that_are_not_the_template()
    {
        var t = await fixture.CreateTenantAsync();
        using var content = new MultipartFormDataContent { { new ByteArrayContent("not excel"u8.ToArray()), "file", "x.xlsx" } };
        var response = await t.Client.PostAsync("/api/hr/imports/employees", content);
        (await response.ProblemAsync()).GetProperty("code").GetString().ShouldBe("import.unreadable");
    }
}

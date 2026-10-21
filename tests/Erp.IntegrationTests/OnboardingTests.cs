using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClosedXML.Excel;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class OnboardingTests(ErpFixture fixture)
{
    public sealed record Step(string Key, int Order, string Link, bool Done);
    public sealed record Steps(bool Dismissed, List<Step> Items);
    public sealed record Line(int Row, string? Code, bool Ok, bool Updated, List<string> Errors);
    public sealed record Summary(bool DryRun, bool Committed, int TotalRows, int ValidRows, int Created, int Updated, List<Line> Rows, List<string> Errors);
    public sealed record Item(string Sku, string NameAr, string? Barcode, decimal? SalesPrice, decimal QuantityOnHand, decimal StockValue);
    public sealed record Page(List<Item> Items);

    private static async Task<List<Item>> ItemsAsync(HttpClient c) => (await c.GetAsync<Page>("/api/inventory/items")).Items;

    /// <summary>An .xlsx like the template: keys in row 1, hints in row 2, data from row 3.</summary>
    private static ByteArrayContent Sheet(string[] keys, params string[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Data");
        for (var i = 0; i < keys.Length; i++) sheet.Cell(1, i + 1).Value = keys[i];
        for (var r = 0; r < rows.Length; r++)
            for (var i = 0; i < rows[r].Length; i++) sheet.Cell(r + 3, i + 1).Value = rows[r][i];
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return content;
    }

    private static async Task<Summary> UploadAsync(HttpClient c, string url, ByteArrayContent file, bool dryRun)
    {
        var form = new MultipartFormDataContent { { file, "file", "import.xlsx" } };
        return await (await c.PostAsync($"{url}?dryRun={dryRun.ToString().ToLowerInvariant()}", form)).ReadAsync<Summary>();
    }

    private static async Task<Steps> StepsAsync(HttpClient c)
    {
        var json = await c.GetAsync<JsonElement>("/api/core/getting-started");
        return new Steps(json.GetProperty("dismissed").GetBoolean(),
            json.GetProperty("steps").Deserialize<List<Step>>(ErpFixture.Json)!);
    }

    [Fact]
    public async Task A_price_list_with_opening_stock_is_checked_first_then_imported_and_updated_on_the_next_import()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        string[] keys = ["Sku", "NameAr", "NameEn", "Type", "Barcode", "SalesPrice", "PurchasePrice", "OpeningQty"];

        // The template downloads as a real Excel file.
        var template = await c.GetAsync("/api/inventory/imports/items/template");
        template.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var wb = new XLWorkbook(await template.Content.ReadAsStreamAsync()))
            wb.Worksheet(1).Cell(1, 1).GetString().ShouldBe("Sku");

        // A bad row: nothing is saved, and every problem is listed.
        var bad = Sheet(keys,
            ["MILK", "حليب", "Milk", "Stock", "6281007012345", "6.5", "5", "24"],
            ["ALTER", "تعديل", "Alteration", "Service", "", "25", "", "3"],
            ["", "بدون رمز", "", "", "", "abc", "", ""]);
        var check = await UploadAsync(c, "/api/inventory/imports/items", bad, dryRun: false);
        check.Committed.ShouldBeFalse();
        check.Rows.Single(r => r.Row == 4).Errors.ShouldContain(e => e.Contains("Services have no stock"));
        check.Rows.Single(r => r.Row == 5).Errors.Count.ShouldBeGreaterThan(0);
        (await ItemsAsync(c)).ShouldBeEmpty();

        // Fixed: a dry run checks without saving, then the import saves items and one opening-stock document.
        var good = () => Sheet(keys,
            ["MILK", "حليب", "Milk", "Stock", "6281007012345", "6.5", "5", "24"],
            ["RICE", "أرز", "Rice", "", "", "32", "25", "10"],
            ["ALTER", "تعديل", "Alteration", "Service", "", "25", "", ""]);
        var dry = await UploadAsync(c, "/api/inventory/imports/items", good(), dryRun: true);
        (dry.ValidRows, dry.Committed).ShouldBe((3, false));
        (await ItemsAsync(c)).ShouldBeEmpty();
        var done = await UploadAsync(c, "/api/inventory/imports/items", good(), dryRun: false);
        (done.Committed, done.Created, done.Updated).ShouldBe((true, 3, 0));
        var items = await ItemsAsync(c);
        items.Single(i => i.Sku == "MILK").QuantityOnHand.ShouldBe(24);
        items.Single(i => i.Sku == "MILK").StockValue.ShouldBe(120);
        items.Single(i => i.Sku == "RICE").StockValue.ShouldBe(250);
        (await c.GetAsync<List<JsonElement>>("/api/inventory/adjustments")).Count.ShouldBe(1);

        // Next month's price list: prices change, blank cells keep what is there, and no second opening stock is allowed.
        var prices = await UploadAsync(c, "/api/inventory/imports/items", Sheet(["Sku", "SalesPrice"], ["MILK", "7"]), dryRun: false);
        (prices.Committed, prices.Updated).ShouldBe((true, 1));
        var milk = (await ItemsAsync(c)).Single(i => i.Sku == "MILK");
        (milk.SalesPrice, milk.Barcode, milk.NameAr, milk.QuantityOnHand).ShouldBe((7m, "6281007012345", "حليب", 24m));
        var again = await UploadAsync(c, "/api/inventory/imports/items", Sheet(["Sku", "OpeningQty"], ["MILK", "5"]), dryRun: false);
        again.Rows.Single().Errors.ShouldContain(e => e.Contains("stock count"));
    }

    [Fact]
    public async Task Customers_and_suppliers_are_imported_and_updated_by_code()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var customers = await UploadAsync(c, "/api/sales/imports/customers",
            Sheet(["Code", "NameAr", "NameEn", "Phone", "CreditLimit", "PaymentTermsDays"],
                ["c-1", "مطعم الريان", "Al Rayyan Restaurant", "+97455501234", "5000", "30"],
                ["C-2", "فاطمة", "Fatima", "", "", "0"]), dryRun: false);
        (customers.Committed, customers.Created).ShouldBe((true, 2));
        var list = await c.GetAsync<List<JsonElement>>("/api/sales/customers");
        var rayyan = list.Single(x => x.GetProperty("code").GetString() == "C-1");
        rayyan.GetProperty("creditLimit").GetDecimal().ShouldBe(5000);

        var update = await UploadAsync(c, "/api/sales/imports/customers", Sheet(["Code", "Phone"], ["C-2", "+97466600000"]), dryRun: false);
        (update.Committed, update.Updated).ShouldBe((true, 1));
        (await c.GetAsync<List<JsonElement>>("/api/sales/customers")).Single(x => x.GetProperty("code").GetString() == "C-2")
            .GetProperty("phone").GetString().ShouldBe("+97466600000");

        var duplicate = await UploadAsync(c, "/api/sales/imports/customers", Sheet(["Code", "NameEn"], ["C-9", "A"], ["c-9", "B"]), dryRun: false);
        duplicate.Committed.ShouldBeFalse();
        duplicate.Rows[1].Errors.ShouldContain(e => e.Contains("twice"));

        var suppliers = await UploadAsync(c, "/api/purchasing/imports/suppliers",
            Sheet(["Code", "NameAr", "NameEn", "Resident"], ["S-1", "الخليج للأغذية", "Gulf Food", "Y"], ["S-2", "مورد هندي", "India Spices", "N"]), dryRun: false);
        (suppliers.Committed, suppliers.Created).ShouldBe((true, 2));
        (await c.GetAsync<List<JsonElement>>("/api/purchasing/suppliers")).Single(x => x.GetProperty("code").GetString() == "S-2")
            .GetProperty("isResident").GetBoolean().ShouldBeFalse();

        // Not an Excel file.
        var junk = new ByteArrayContent("hello"u8.ToArray());
        var form = new MultipartFormDataContent { { junk, "file", "x.xlsx" } };
        (await (await c.PostAsync("/api/sales/imports/customers", form)).ProblemAsync()).GetProperty("code").GetString().ShouldBe("import.unreadable");
    }

    [Fact]
    public async Task Getting_started_follows_the_shops_progress_and_can_be_hidden()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var start = await StepsAsync(c);
        start.Dismissed.ShouldBeFalse();
        start.Items.Select(s => s.Key).ShouldBe(["company", "users", "items", "stock", "till", "first-sale", "employees", "payroll"]);
        start.Items.ShouldAllBe(s => !s.Done);

        await UploadAsync(c, "/api/inventory/imports/items", Sheet(["Sku", "NameAr", "PurchasePrice", "OpeningQty"], ["TEA", "شاي", "3", "10"]), dryRun: false);
        await (await c.PostJsonAsync("/api/core/users", new
        {
            email = $"cashier-{Guid.NewGuid():N}@t.qa", displayNameEn = "Cashier", password = "User-Password-123", preferredLanguage = "en", roles = new[] { "salesperson" }
        })).EnsureSuccessAsync();
        var later = await StepsAsync(c);
        later.Items.Where(s => s.Done).Select(s => s.Key).ShouldBe(["users", "items", "stock"], ignoreOrder: true);

        // Switching a module off removes its steps.
        await (await c.PutJsonAsync("/api/core/modules", new { disabled = new[] { "hr" } })).EnsureSuccessAsync();
        (await StepsAsync(c)).Items.Select(s => s.Key).ShouldNotContain("payroll");

        await (await c.PostJsonAsync("/api/core/getting-started/hide", new { })).EnsureSuccessAsync();
        (await StepsAsync(c)).Dismissed.ShouldBeTrue();
        await (await c.PostJsonAsync("/api/core/getting-started/show", new { })).EnsureSuccessAsync();
        (await StepsAsync(c)).Dismissed.ShouldBeFalse();
    }
}

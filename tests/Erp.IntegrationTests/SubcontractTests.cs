using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SubcontractTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Item(Guid Id, string DescriptionEn);
    public sealed record Contract(Guid Id, decimal ContractValue, decimal TaxRetentionPercent, List<Item> Items, decimal RetentionHeld, decimal RetentionReleased,
        decimal TaxRetentionHeld, decimal TaxRetentionReleased, decimal PercentComplete);
    public sealed record Certificate(Guid Id, string Status, decimal Gross, decimal Retention, decimal TaxRetention, decimal Net, Guid? SupplierInvoiceId);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Subcontractor_certificates_hold_retention_and_tax_clearance_retention_until_released()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var supplier = await PurchasingTests.SupplierAsync(c, "S-CIVIL");
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C-TOWER", nameEn = "Tower owner", nameAr = "مالك البرج", paymentTermsDays = 30 }))
            .GetProperty("id").GetGuid();
        var project = (await c.PostAsync<JsonElement>("/api/sales/projects", new
        {
            code = "P-TOWER", nameEn = "Lusail tower", nameAr = "برج لوسيل", customerId = customer, retentionPercent = 0m, budgetCost = 50_000m, status = "Active",
            items = new[] { new { descriptionEn = "Structure", descriptionAr = "الهيكل", unit = "lot", quantity = 1m, rate = 60_000m } }
        })).GetProperty("id").GetGuid();

        // 100 m3 at 200 = 20,000; 10% retention; tax-clearance retention from the purchasing setting (3%).
        var contract = await c.PostAsync<Contract>("/api/purchasing/subcontracts", new
        {
            code = "SC-CONC", nameEn = "Concrete works", nameAr = "أعمال الخرسانة", supplierId = supplier, projectId = project, retentionPercent = 10m,
            status = "Active", items = new[] { new { descriptionEn = "Concrete", descriptionAr = "خرسانة", unit = "m3", quantity = 100m, rate = 200m } }
        });
        contract.ContractValue.ShouldBe(20_000m);
        contract.TaxRetentionPercent.ShouldBe(3m);
        var concrete = contract.Items[0].Id;

        var first = await c.PostAsync<Certificate>($"/api/purchasing/subcontracts/{contract.Id}/certificates", new
        {
            periodEnd = Today, progress = new[] { new { itemId = concrete, quantityToDate = 50m } }
        });
        first.Gross.ShouldBe(10_000m);
        first.Retention.ShouldBe(1_000m);
        first.TaxRetention.ShouldBe(300m);
        first.Net.ShouldBe(8_700m);
        first = await c.PostAsync<Certificate>($"/api/purchasing/subcontracts/{contract.Id}/certificates/{first.Id}/invoice", new { date = Today, supplierRef = "INV-77" });
        first.Status.ShouldBe("Invoiced");
        (await ClosingAsync(c, "2110")).ShouldBe(-8_700m);
        (await ClosingAsync(c, "2135")).ShouldBe(-1_000m);
        (await ClosingAsync(c, "2175")).ShouldBe(-300m);
        (await ClosingAsync(c, "5360")).ShouldBe(10_000m);
        // Only the net is payable; the cost lands on the customer project.
        var invoice = await c.GetAsync<JsonElement>($"/api/purchasing/invoices/{first.SupplierInvoiceId}");
        invoice.GetProperty("openAmount").GetDecimal().ShouldBe(8_700m);
        (await c.GetAsync<JsonElement>($"/api/sales/projects/{project}/profit")).GetProperty("cost").GetDecimal().ShouldBe(10_000m);

        // Retention-release lines cannot be keyed directly on a supplier invoice.
        (await (await c.PostJsonAsync("/api/purchasing/invoices", new
        {
            supplierId = supplier, supplierRef = "X", invoiceDate = Today, lines = new[] { new { lineType = "RetentionRelease", description = "x", quantity = 1m, unitPrice = 1m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.release_via_subcontract");

        // The subcontractor shows a tax clearance certificate: the 300 becomes payable and later certificates hold no tax retention.
        await c.PostAsync<JsonElement>($"/api/purchasing/subcontracts/{contract.Id}/tax-clearance", new { receivedOn = Today, releaseDate = Today });
        (await ClosingAsync(c, "2175")).ShouldBe(0m);
        (await ClosingAsync(c, "2110")).ShouldBe(-9_000m);

        var second = await c.PostAsync<Certificate>($"/api/purchasing/subcontracts/{contract.Id}/certificates", new
        {
            periodEnd = Today, progress = new[] { new { itemId = concrete, quantityToDate = 100m } }
        });
        second.TaxRetention.ShouldBe(0m);
        second.Net.ShouldBe(9_000m);
        await c.PostAsync<Certificate>($"/api/purchasing/subcontracts/{contract.Id}/certificates/{second.Id}/invoice", new { date = Today });

        // Handover: all retention released.
        (await (await c.PostJsonAsync($"/api/purchasing/subcontracts/{contract.Id}/release-retention", new { date = Today, amount = 2_500m }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("pur.retention_release");
        await c.PostAsync<JsonElement>($"/api/purchasing/subcontracts/{contract.Id}/release-retention", new { date = Today, amount = 2_000m });
        (await ClosingAsync(c, "2135")).ShouldBe(0m);
        (await ClosingAsync(c, "2110")).ShouldBe(-20_000m);
        (await ClosingAsync(c, "5360")).ShouldBe(20_000m);

        contract = await c.GetAsync<Contract>($"/api/purchasing/subcontracts/{contract.Id}");
        contract.PercentComplete.ShouldBe(100m);
        contract.RetentionHeld.ShouldBe(2_000m);
        contract.RetentionReleased.ShouldBe(2_000m);
        contract.TaxRetentionHeld.ShouldBe(300m);
        contract.TaxRetentionReleased.ShouldBe(300m);
        var aging = await c.GetAsync<JsonElement>($"/api/purchasing/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(aging.GetProperty("ledgerBalance").GetDecimal());
        aging.GetProperty("total").GetDecimal().ShouldBe(20_000m);
    }
}

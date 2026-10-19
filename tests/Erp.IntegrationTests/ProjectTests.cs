using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class ProjectTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Item(Guid Id, string DescriptionEn, decimal Quantity, decimal CertifiedQuantity, decimal PercentComplete);
    public sealed record Project(Guid Id, decimal ContractValue, List<Item> Items, decimal CertifiedToDate, decimal PercentComplete, decimal RetentionHeld,
        decimal RetentionReleased);
    public sealed record Certificate(Guid Id, string CertificateNo, string Status, decimal Gross, decimal Retention, decimal Net, decimal GrossToDate,
        Guid? SalesInvoiceId);
    public sealed record Profit(decimal Revenue, decimal Cost, decimal Profit_, decimal CostToBudgetPercent);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Progress_certificates_bill_the_work_with_retention_and_the_project_shows_its_profit()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C-VILLA", nameEn = "Villa owner", nameAr = "مالك الفيلا", paymentTermsDays = 30 }))
            .GetProperty("id").GetGuid();

        var project = await c.PostAsync<Project>("/api/sales/projects", new
        {
            code = "P-WAKRA", nameEn = "Al Wakra villa", nameAr = "فيلا الوكرة", customerId = customer, retentionPercent = 10m, budgetCost = 60_000m,
            status = "Active", items = new object[]
            {
                new { section = "Finishes", descriptionEn = "Floor tiling", descriptionAr = "تبليط الأرضيات", unit = "m2", quantity = 1_000m, rate = 50m },
                new { section = "MEP", descriptionEn = "Plumbing", descriptionAr = "السباكة", unit = "lot", quantity = 1m, rate = 30_000m }
            }
        });
        project.ContractValue.ShouldBe(80_000m);
        var tiling = project.Items.Single(i => i.DescriptionEn == "Floor tiling").Id;
        var plumbing = project.Items.Single(i => i.DescriptionEn == "Plumbing").Id;

        // Certificate 1: 400 m2 of tiling = 20,000, less 10% retention.
        var first = await c.PostAsync<Certificate>($"/api/sales/projects/{project.Id}/certificates", new
        {
            periodEnd = Today, progress = new[] { new { boqItemId = tiling, quantityToDate = 400m } }
        });
        first.Gross.ShouldBe(20_000m);
        first.Retention.ShouldBe(2_000m);
        first = await c.PostAsync<Certificate>($"/api/sales/projects/{project.Id}/certificates/{first.Id}/invoice", new { date = Today });
        first.Status.ShouldBe("Invoiced");
        (await ClosingAsync(c, "1130")).ShouldBe(18_000m);
        (await ClosingAsync(c, "1145")).ShouldBe(2_000m);
        (await ClosingAsync(c, "4120")).ShouldBe(-20_000m);

        // Quantities to date cannot go backwards or beyond the contract.
        (await (await c.PostJsonAsync($"/api/sales/projects/{project.Id}/certificates", new
        {
            periodEnd = Today, progress = new[] { new { boqItemId = tiling, quantityToDate = 300m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.progress_back");
        (await (await c.PostJsonAsync($"/api/sales/projects/{project.Id}/certificates", new
        {
            periodEnd = Today, progress = new[] { new { boqItemId = tiling, quantityToDate = 1_200m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.progress_over");

        // Certificate 2: tiling complete and plumbing done = 30,000 + 30,000.
        var second = await c.PostAsync<Certificate>($"/api/sales/projects/{project.Id}/certificates", new
        {
            periodEnd = Today, progress = new[] { new { boqItemId = tiling, quantityToDate = 1_000m }, new { boqItemId = plumbing, quantityToDate = 1m } }
        });
        second.Gross.ShouldBe(60_000m);
        second.GrossToDate.ShouldBe(80_000m);
        await c.PostAsync<Certificate>($"/api/sales/projects/{project.Id}/certificates/{second.Id}/invoice", new { date = Today });
        project = await c.GetAsync<Project>($"/api/sales/projects/{project.Id}");
        project.PercentComplete.ShouldBe(100m);
        project.RetentionHeld.ShouldBe(8_000m);

        // Costs: a subcontractor invoice and site expenses charged to the project.
        var supplier = await PurchasingTests.SupplierAsync(c, "S-SUB");
        var invoice = await c.PostAsync<JsonElement>("/api/purchasing/invoices", new
        {
            supplierId = supplier, supplierRef = "SUB-1", invoiceDate = Today, projectId = project.Id,
            lines = new[] { new { lineType = "Service", description = "Plumbing subcontract", quantity = 1m, unitPrice = 25_000m } }
        });
        await c.PostAsync<JsonElement>($"/api/purchasing/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
        await c.PostAsync<FinanceTests.Voucher>("/api/finance/journal-vouchers", new
        {
            date = Today, memoEn = "Site expenses", lines = new object[]
            {
                new { accountId = A("5360"), debit = 5_000m, credit = 0m, projectId = project.Id }, new { accountId = A("1110"), debit = 0m, credit = 5_000m }
            }
        });
        var profit = await c.GetAsync<JsonElement>($"/api/sales/projects/{project.Id}/profit");
        profit.GetProperty("revenue").GetDecimal().ShouldBe(80_000m);
        profit.GetProperty("cost").GetDecimal().ShouldBe(30_000m);
        profit.GetProperty("profit").GetDecimal().ShouldBe(50_000m);
        profit.GetProperty("costToBudgetPercent").GetDecimal().ShouldBe(50m);

        // Handover: the retention is billed; no revenue, it moves from retentions to receivables.
        await c.PostAsync<JsonElement>($"/api/sales/projects/{project.Id}/release-retention", new { date = Today, amount = 8_000m });
        (await ClosingAsync(c, "1145")).ShouldBe(0m);
        (await ClosingAsync(c, "1130")).ShouldBe(80_000m);
        (await ClosingAsync(c, "4120")).ShouldBe(-80_000m);
        (await c.GetAsync<Project>($"/api/sales/projects/{project.Id}")).RetentionReleased.ShouldBe(8_000m);
        var aging = await c.GetAsync<JsonElement>($"/api/sales/reports/aging?asOf={Today:yyyy-MM-dd}");
        aging.GetProperty("total").GetDecimal().ShouldBe(aging.GetProperty("ledgerBalance").GetDecimal());
    }
}

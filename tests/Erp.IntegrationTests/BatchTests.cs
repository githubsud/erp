using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class BatchTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record BatchRow(string Sku, string BatchNo, DateOnly? ExpiryDate, string WarehouseCode, decimal Quantity, int? DaysLeft);

    [Fact]
    public async Task Batches_are_issued_first_expiry_first_and_expired_stock_is_only_written_off()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var yard = (await c.PostAsync<JsonElement>("/api/inventory/warehouses", new { code = "QUAR", nameEn = "Quarantine", nameAr = "الحجر" }))
            .GetProperty("id").GetGuid();
        var milk = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "MILK", nameEn = "Milk powder", nameAr = "حليب بودرة", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 20m,
            trackBatches = true
        })).Id;

        // Opening stock in three batches; one already expired.
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new object[]
            {
                new { itemId = milk, quantity = 5m, unitCost = 10m, batchNo = "OLD", expiryDate = Today.AddDays(-1) },
                new { itemId = milk, quantity = 10m, unitCost = 10m, batchNo = "b-late", expiryDate = Today.AddDays(40) },
                new { itemId = milk, quantity = 10m, unitCost = 10m, batchNo = "B-SOON", expiryDate = Today.AddDays(10) }
            }
        })).EnsureSuccessStatusCode();
        var batches = await c.GetAsync<List<BatchRow>>($"/api/inventory/batches?itemId={milk}");
        batches.Select(b => (b.BatchNo, b.Quantity)).ShouldBe([("OLD", 5m), ("B-SOON", 10m), ("B-LATE", 10m)]);

        // An accountant sells 12 at the counter: B-SOON first, then B-LATE; the expired batch is skipped.
        await (await c.PostJsonAsync("/api/core/users", new
        {
            email = "acc@t.qa", displayNameEn = "Accountant", password = "User-Password-123", preferredLanguage = "en", roles = new[] { "accountant" }
        })).EnsureSuccessAsync();
        var accountant = await fixture.LoginAsync(t.Code, "acc@t.qa", "User-Password-123");
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C1", nameEn = "Cafe", nameAr = "مقهى", paymentTermsDays = 0 }))
            .GetProperty("id").GetGuid();
        var sale = await accountant.PostAsync<SalesTests.Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, warehouseId = main, lines = new[] { new { itemId = milk, quantity = 12m } }
        });
        await accountant.PostAsync<SalesTests.Invoice>($"/api/sales/invoices/{sale.Id}/post", new { });
        batches = await c.GetAsync<List<BatchRow>>($"/api/inventory/batches?itemId={milk}&warehouseId={main}");
        batches.Select(b => (b.BatchNo, b.Quantity)).ShouldBe([("OLD", 5m), ("B-LATE", 8m)]);

        // Selling more than the unexpired 8 is refused rather than handing out expired goods.
        var tooMuch = await accountant.PostAsync<SalesTests.Invoice>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, warehouseId = main, lines = new[] { new { itemId = milk, quantity = 10m } }
        });
        (await (await accountant.PostJsonAsync($"/api/sales/invoices/{tooMuch.Id}/post", new { })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("inv.batch_expired");

        // The expiry report lists the expired and the soon-expiring batches.
        var expiring = await c.GetAsync<List<BatchRow>>("/api/inventory/reports/expiry?withinDays=60");
        expiring.Select(b => (b.BatchNo, b.DaysLeft)).ShouldBe([("OLD", (int?)-1), ("B-LATE", (int?)40)]);

        // Moving the expired batch to quarantine keeps its batch; writing it off there clears it.
        (await c.PostJsonAsync("/api/inventory/transfers", new
        {
            fromWarehouseId = main, toWarehouseId = yard, date = Today, lines = new[] { new { itemId = milk, quantity = 5m } }
        })).EnsureSuccessStatusCode();
        (await c.GetAsync<List<BatchRow>>($"/api/inventory/batches?itemId={milk}&warehouseId={yard}")).Single().BatchNo.ShouldBe("OLD");
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = yard, date = Today, reason = "Expiry", lines = new[] { new { itemId = milk, quantity = -5m, batchNo = "OLD" } }
        })).EnsureSuccessStatusCode();
        (await c.GetAsync<List<BatchRow>>($"/api/inventory/batches?itemId={milk}")).Select(b => b.BatchNo).ShouldBe(["B-LATE"]);

        // Reversing the sale puts the goods back into the batches they came from.
        (await accountant.PostJsonAsync($"/api/sales/invoices/{sale.Id}/reverse", new { date = Today, reason = "Wrong customer" })).EnsureSuccessStatusCode();
        (await c.GetAsync<List<BatchRow>>($"/api/inventory/batches?itemId={milk}")).Select(b => (b.BatchNo, b.Quantity))
            .ShouldBe([("B-SOON", 10m), ("B-LATE", 10m)]);

        // Batch tracking cannot be switched off while there is stock.
        var off = await c.PutAsJsonAsync($"/api/inventory/items/{milk}", new
        {
            sku = "MILK", nameEn = "Milk powder", nameAr = "حليب بودرة", itemType = "Stock", itemGroupId = group, uomId = pcs, trackBatches = false
        }, ErpFixture.Json);
        (await off.ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.batches_locked");
    }
}

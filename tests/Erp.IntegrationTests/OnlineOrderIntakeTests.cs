using System.Net;
using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.IntegrationTests.Infrastructure;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

/// <summary>Online orders enter Sales as confirmed sales orders on channel online; delivery, invoicing and receipts are the usual ones.</summary>
[Collection(ErpCollection.Name)]
public sealed class OnlineOrderIntakeTests(ErpFixture fixture)
{
    [Fact]
    public async Task Online_orders_become_confirmed_sales_orders_and_count_as_promised_stock()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var plug = await CommerceAdminTests.ItemAsync(c, "PLUG", 10m);
        var delivery = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "DELIVERY", nameEn = "Delivery", nameAr = "توصيل", itemType = "Service", itemGroupId = group, uomId = pcs, salesPrice = 0m
        })).Id;
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = FinanceTests.Today, reason = "Opening", lines = new[] { new { itemId = plug, quantity = 10m, unitCost = 6m } }
        })).EnsureSuccessStatusCode();
        // A known customer with a tiny credit limit: cash on delivery is not credit, so it is not held.
        var known = (await c.PostAsync<JsonElement>("/api/sales/customers", new
        {
            code = "KNOWN", nameEn = "Known", nameAr = "معروف", phone = "55001122", creditLimit = 1m
        })).GetProperty("id").GetGuid();

        async Task<T> InScopeAsync<T>(Func<IOnlineOrderIntake, ErpDbContext, Task<T>> work)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(t.TenantId, t.CompanyId, SystemUser.Id);
            var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
            return await work(scope.ServiceProvider.GetRequiredService<IOnlineOrderIntake>(), db);
        }
        Task<OnlineOrderResult> PlaceAsync(string reference, string name, string phone, decimal quantity) =>
            InScopeAsync((intake, db) => db.InTransactionAsync(token => intake.PlaceAsync(new OnlineOrderRequest(name, phone, main, reference, FinanceTests.Today,
                "Zone 25, street 900, building 12", [new OnlineOrderLine(plug, quantity, UnitPrice: 9.5m), new OnlineOrderLine(delivery, 1m, UnitPrice: 15m)]), token)));

        // Outside a transaction it refuses to run.
        await Should.ThrowAsync<InvalidOperationException>(() => InScopeAsync((intake, _) =>
            intake.PlaceAsync(new OnlineOrderRequest("X", "55990000", main, "W-0", FinanceTests.Today, null, [new OnlineOrderLine(plug, 1m)]))));

        var first = await PlaceAsync("W-1001", "Sara", "+974 5512 3456", 3m);
        first.Total.ShouldBe(3 * 9.5m + 15m);
        var order = await c.GetAsync<JsonElement>($"/api/sales/orders/{first.SalesOrderId}");
        order.GetProperty("status").GetString().ShouldBe("Confirmed");
        order.GetProperty("channel").GetString().ShouldBe("Online");
        order.GetProperty("externalRef").GetString().ShouldBe("W-1001");
        order.GetProperty("customerCode").GetString().ShouldBe("WEB55123456");

        // The same phone is the same customer; a known customer's phone finds that customer, and the credit limit does not hold it.
        (await PlaceAsync("W-1002", "Sara", "55123456", 1m)).CustomerId.ShouldBe(first.CustomerId);
        var knownOrder = await PlaceAsync("W-1003", "Known", "974 5500 1122", 2m);
        knownOrder.CustomerId.ShouldBe(known);
        (await c.GetAsync<JsonElement>($"/api/sales/orders/{knownOrder.SalesOrderId}")).GetProperty("status").GetString().ShouldBe("Confirmed");
        (await Should.ThrowAsync<ConflictException>(() => PlaceAsync("W-1001", "Sara", "55123456", 1m))).Code.ShouldBe("sal.online_order_exists");

        // Promised stock: 3 + 1 + 2 pieces still to deliver (services do not count).
        var undelivered = await InScopeAsync((intake, _) => intake.GetUndeliveredQuantitiesAsync([plug, delivery]));
        undelivered[plug].ShouldBe(6m);
        undelivered.ContainsKey(delivery).ShouldBeFalse();

        // Deliver and invoice the first order the usual way; then it no longer counts and only the receipt is outstanding.
        var lines = order.GetProperty("lines").EnumerateArray().ToList();
        var plugLine = lines.Single(l => l.GetProperty("itemId").GetGuid() == plug).GetProperty("id").GetGuid();
        var feeLine = lines.Single(l => l.GetProperty("itemId").GetGuid() == delivery).GetProperty("id").GetGuid();
        var note = await c.PostAsync<JsonElement>("/api/sales/deliveries", new
        {
            salesOrderId = first.SalesOrderId, deliveryDate = FinanceTests.Today, lines = new[] { new { salesOrderLineId = plugLine, quantity = 3m } }
        });
        var noteLine = note.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var invoice = await c.PostAsync<JsonElement>("/api/sales/invoices", new
        {
            customerId = first.CustomerId, invoiceDate = FinanceTests.Today,
            lines = new object[] { new { deliveryNoteLineId = noteLine, quantity = 3m }, new { salesOrderLineId = feeLine, quantity = 1m } }
        });
        await c.PostAsync<JsonElement>($"/api/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
        var status = (await InScopeAsync((intake, _) => intake.GetStatusAsync([first.SalesOrderId])))[first.SalesOrderId];
        status.FullyDelivered.ShouldBeTrue();
        status.FullyInvoiced.ShouldBeTrue();
        status.Outstanding.ShouldBe(43.5m);
        (await InScopeAsync((intake, _) => intake.GetUndeliveredQuantitiesAsync([plug])))[plug].ShouldBe(3m);
        (await c.GetAsync("/api/sales/orders/" + Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

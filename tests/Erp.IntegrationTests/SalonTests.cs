using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SalonTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Appointments_check_out_into_paid_invoices_and_commission_is_reported_per_staff()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        var cash = accounts.Single(a => a.Code == "1110").Id;
        var (_, group, pcs) = await InventoryTests.DefaultsAsync(c);
        async Task<Guid> Service(string sku, string en, string ar, decimal price) =>
            (await c.PostAsync<JsonElement>("/api/inventory/items", new { sku, nameEn = en, nameAr = ar, itemType = "Service", itemGroupId = group, uomId = pcs, salesPrice = price }))
            .GetProperty("id").GetGuid();
        var haircut = await Service("CUT", "Haircut", "قص شعر", 80m);
        var colour = await Service("COLOUR", "Hair colour", "صبغة شعر", 250m);
        var services = await c.GetAsync<List<JsonElement>>("/api/salon/services");
        services.Count.ShouldBe(2);

        var sara = await c.PostAsync<Guid>("/api/salon/staff", new { name = "Sara", commissionPercent = 10m, color = "#f8bbd0" });
        var lina = await c.PostAsync<Guid>("/api/salon/staff", new { name = "Lina", commissionPercent = 20m });

        var at = new DateTimeOffset(Today.ToDateTime(new TimeOnly(16, 0)), TimeSpan.FromHours(3));
        var booking = await c.PostAsync<Guid>("/api/salon/appointments", new
        {
            customerName = "Noora", phone = "33334444", staffId = sara, itemId = colour, startsAt = at, minutes = 90
        });
        (await (await c.PostJsonAsync("/api/salon/appointments", new { customerName = "Hessa", staffId = sara, startsAt = at.AddMinutes(30), minutes = 30 }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.salon_clash");
        await c.PostAsync<Guid>("/api/salon/appointments", new { customerName = "Hessa", staffId = lina, startsAt = at.AddMinutes(30), minutes = 30 });
        var day = await c.GetAsync<List<JsonElement>>($"/api/salon/appointments?day={Today:yyyy-MM-dd}");
        day.Count.ShouldBe(2);
        day.Single(a => a.GetProperty("id").GetGuid() == booking).GetProperty("whatsAppPhone").GetString().ShouldBe("97433334444");

        // Colour by Sara (250) and a haircut by Lina at 10% off (72): paid 322 cash.
        var visit = await c.PostAsync<JsonElement>("/api/salon/checkout", new
        {
            appointmentId = booking, date = Today, accountId = cash, method = "Cash",
            lines = new object[] { new { itemId = colour, staffId = sara }, new { itemId = haircut, staffId = lina, discountPercent = 10m } }
        });
        visit.GetProperty("total").GetDecimal().ShouldBe(322m);
        visit.GetProperty("commission").GetDecimal().ShouldBe(25m + 14.4m);
        var invoice = await c.GetAsync<JsonElement>($"/api/sales/invoices/{visit.GetProperty("salesInvoiceId").GetGuid()}");
        invoice.GetProperty("status").GetString().ShouldBe("Paid");
        (await ClosingAsync(c, "1110")).ShouldBe(322m);
        (await ClosingAsync(c, "1130")).ShouldBe(0m);
        (await c.PostJsonAsync("/api/salon/checkout", new
        {
            appointmentId = booking, date = Today, accountId = cash, lines = new[] { new { itemId = haircut, staffId = sara } }
        })).StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);

        // A walk-in with no phone goes to the shop's walk-in customer.
        await c.PostAsync<JsonElement>("/api/salon/checkout", new
        {
            date = Today, accountId = cash, lines = new[] { new { itemId = haircut, staffId = lina } }
        });
        var commission = await c.GetAsync<List<JsonElement>>($"/api/salon/commission?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        commission.Single(x => x.GetProperty("staffName").GetString() == "Lina").GetProperty("commission").GetDecimal().ShouldBe(14.4m + 16m);
        commission.Single(x => x.GetProperty("staffName").GetString() == "Sara").GetProperty("sales").GetDecimal().ShouldBe(250m);
        (await c.GetAsync<List<JsonElement>>($"/api/salon/appointments?day={Today:yyyy-MM-dd}"))
            .Single(a => a.GetProperty("id").GetGuid() == booking).GetProperty("status").GetString().ShouldBe("Done");
    }
}

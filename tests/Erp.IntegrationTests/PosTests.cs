using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class PosTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Shift(Guid Id, string ShiftNo, string Status, decimal OpeningFloat, decimal? ExpectedCash, decimal? CountedCash, decimal? Difference);
    public sealed record SaleLine(Guid Id, Guid ItemId, decimal Quantity, decimal Amount, decimal Refundable);
    public sealed record Sale(Guid Id, string ReceiptNo, decimal Total, decimal DiscountTotal, decimal CashAmount, decimal CardAmount, decimal ChangeGiven,
        bool IsRefund, List<SaleLine> Lines);
    public sealed record ZReport(Shift Shift, int SalesCount, int RefundCount, decimal GrossSales, decimal Discounts, decimal Refunds, decimal NetSales,
        decimal CashSales, decimal CardSales, decimal ExpectedCash, decimal? Difference, decimal Cost);

    private static async Task<decimal> ClosingAsync(HttpClient c, string code) =>
        (await c.GetAsync<FinanceTests.Tb>($"/api/finance/reports/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))
        .Rows.SingleOrDefault(r => r.Code == code)?.Closing ?? 0m;

    [Fact]
    public async Task Till_sales_refunds_and_shift_close_post_to_the_ledger_and_offline_resends_post_once()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var item = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "COLA", nameEn = "Cola can", nameAr = "علبة كولا", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 3m
        })).Id;
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = Today, reason = "Opening", lines = new[] { new { itemId = item, quantity = 100m, unitCost = 1m } }
        })).EnsureSuccessStatusCode();

        var register = await c.PostAsync<Guid>("/api/sales/pos/registers", new
        {
            code = "T1", nameEn = "Till 1", nameAr = "صندوق 1", warehouseId = main, cashAccountId = A("1110"), cardAccountId = A("1115")
        });
        var shift = await c.PostAsync<Shift>("/api/sales/pos/shifts", new { registerId = register, openingFloat = 200m });
        (await (await c.PostJsonAsync("/api/sales/pos/shifts", new { registerId = register, openingFloat = 0m })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("sal.shift_open");

        // 10 cans with 10% off = 27; paid 50 cash → 23 change.
        var clientId = Guid.NewGuid();
        object SaleBody() => new
        {
            clientId, shiftId = shift.Id, lines = new[] { new { itemId = item, quantity = 10m, discountPercent = 10m } }, cash = 50m, card = 0m
        };
        var sale = await c.PostAsync<Sale>("/api/sales/pos/sales", SaleBody());
        sale.Total.ShouldBe(27m);
        sale.DiscountTotal.ShouldBe(3m);
        sale.ChangeGiven.ShouldBe(23m);
        // The till resends the same sale after a dropped connection: nothing posts twice.
        (await c.PostAsync<Sale>("/api/sales/pos/sales", SaleBody())).Id.ShouldBe(sale.Id);

        // Split payment: 5 cans = 15, 10 by card and 5 cash.
        await c.PostAsync<Sale>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, lines = new[] { new { itemId = item, quantity = 5m } }, cash = 5m, card = 10m
        });
        (await (await c.PostJsonAsync("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, lines = new[] { new { itemId = item, quantity = 5m } }, cash = 5m, card = 0m
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.pos_short");

        // Refund 4 of the first sale's 10 cans in cash: 27 × 4/10 = 10.80.
        var refund = await c.PostAsync<Sale>("/api/sales/pos/refunds", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, saleId = sale.Id, lines = new[] { new { saleLineId = sale.Lines[0].Id, quantity = 4m } }
        });
        refund.Total.ShouldBe(-10.8m);
        refund.IsRefund.ShouldBeTrue();
        (await c.GetAsync<Sale>($"/api/sales/pos/sales/by-receipt/{sale.ReceiptNo}")).Lines[0].Refundable.ShouldBe(6m);
        (await (await c.PostJsonAsync("/api/sales/pos/refunds", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, saleId = sale.Id, lines = new[] { new { saleLineId = sale.Lines[0].Id, quantity = 7m } }
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.pos_over_refund");

        // Ledger: revenue 27 + 15 − 10.80; stock 100 − 15 + 4 = 89 at 1.00; card clearing 10.
        (await ClosingAsync(c, "4110")).ShouldBe(-31.2m);
        (await ClosingAsync(c, "1115")).ShouldBe(10m);
        (await ClosingAsync(c, "5110")).ShouldBe(11m);
        (await ClosingAsync(c, "1150")).ShouldBe(89m);

        // Till should hold 200 + 27 + 5 − 10.80 = 221.20; the cashier counts 220 → 1.20 short.
        var z = await c.PostAsync<ZReport>($"/api/sales/pos/shifts/{shift.Id}/close", new { countedCash = 220m });
        z.Shift.Status.ShouldBe("Closed");
        z.ExpectedCash.ShouldBe(221.2m);
        z.Difference.ShouldBe(-1.2m);
        z.SalesCount.ShouldBe(2);
        z.RefundCount.ShouldBe(1);
        z.GrossSales.ShouldBe(45m);
        z.Discounts.ShouldBe(3m);
        z.Refunds.ShouldBe(10.8m);
        z.NetSales.ShouldBe(31.2m);
        z.CardSales.ShouldBe(10m);
        z.Cost.ShouldBe(11m);
        (await ClosingAsync(c, "5345")).ShouldBe(1.2m);
        // Cash ledger: the float was never booked, so 1110 holds takings less the shortage: 27 + 5 − 10.80 − 1.20.
        (await ClosingAsync(c, "1110")).ShouldBe(20m);

        (await (await c.PostJsonAsync("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift.Id, lines = new[] { new { itemId = item, quantity = 1m } }, cash = 3m, card = 0m
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.shift_closed");
        var valuation = await c.GetAsync<InventoryTests.Valuation>($"/api/inventory/reports/valuation?asOf={Today:yyyy-MM-dd}");
        valuation.Difference.ShouldBe(0m);
    }
}

using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

/// <summary>
/// <see cref="ISalesPricing"/> (what the online store charges) prices exactly like a quotation for the same customer, and like the
/// till for a guest; it does not change how either prices.
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class SalesPricingTests(ErpFixture fixture)
{
    [Fact]
    public async Task Sales_pricing_matches_quotations_and_the_till()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var box = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "BOX12", nameEn = "Box of 12", nameAr = "علبة 12" })).Id;
        var plug = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "PLUG", nameEn = "Spark plug", nameAr = "بوجي", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 10.5m
        })).Id;
        await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{plug}/units", new object[] { new { uomId = box, factor = 12m, salesPrice = 100m } });
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = FinanceTests.Today, reason = "Opening", lines = new[] { new { itemId = plug, quantity = 100m, unitCost = 6m } }
        })).EnsureSuccessStatusCode();

        var walkIn = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "WALK", nameEn = "Walk-in", nameAr = "عميل نقدي" })).GetProperty("id").GetGuid();
        var garages = (await c.PostAsync<JsonElement>("/api/sales/price-lists", new
        {
            code = "GARAGE", nameEn = "Garages", nameAr = "الكراجات", isActive = true,
            items = new object[] { new { itemId = plug, unitPrice = 8m }, new { itemId = plug, unitPrice = 90m, uomId = box } }
        })).GetProperty("id").GetGuid();
        var garage = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "GAR", nameEn = "Garage", nameAr = "كراج", priceListId = garages }))
            .GetProperty("id").GetGuid();

        BasketLine[] basket = [new(plug, 3m), new(plug, 2m, box)];
        async Task<PricedBasket> PriceAsync(Guid? customer)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(t.TenantId, t.CompanyId, t.AdminUserId);
            return await scope.ServiceProvider.GetRequiredService<ISalesPricing>().PriceAsync(customer, basket);
        }
        async Task<JsonElement> QuoteAsync(Guid customer) => await c.PostAsync<JsonElement>("/api/sales/quotations", new
        {
            customerId = customer, quoteDate = FinanceTests.Today, lines = basket.Select(l => new { itemId = l.ItemId, quantity = l.Quantity, uomId = l.UomId })
        });
        static void ShouldMatch(PricedBasket priced, JsonElement document)
        {
            priced.Total.ShouldBe(document.GetProperty("total").GetDecimal());
            var lines = document.GetProperty("lines").EnumerateArray().ToList();
            priced.Lines.Select(l => l.Amount).ShouldBe(lines.Select(l => l.GetProperty("amount").GetDecimal()));
            priced.Lines.Select(l => l.BaseQuantity).ShouldBe(lines.Select(l => l.GetProperty("quantity").GetDecimal()));
        }

        // A guest pays the item's price: 3 × 10.5 + 2 boxes × 100, the same as a quotation for a customer without a price list.
        var guest = await PriceAsync(null);
        guest.Total.ShouldBe(231.5m);
        guest.Lines.Select(l => l.UnitPrice).ShouldBe([10.5m, 100m]);
        guest.Lines.Select(l => l.BaseQuantity).ShouldBe([3m, 24m]);
        ShouldMatch(guest, await QuoteAsync(walkIn));
        ShouldMatch(await PriceAsync(walkIn), await QuoteAsync(walkIn));

        // The till, with no prices typed in, charges a guest the same.
        var register = await c.PostAsync<Guid>("/api/sales/pos/registers", new
        {
            code = "T1", nameEn = "Till 1", nameAr = "صندوق 1", warehouseId = main, cashAccountId = A("1110"), cardAccountId = A("1115")
        });
        var shift = (await c.PostAsync<JsonElement>("/api/sales/pos/shifts", new { registerId = register, openingFloat = 0m })).GetProperty("id").GetGuid();
        var sale = await c.PostAsync<JsonElement>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift, cash = 231.5m, card = 0m,
            lines = basket.Select(l => new { itemId = l.ItemId, quantity = l.Quantity, uomId = l.UomId })
        });
        ShouldMatch(guest, sale);

        // A customer on a price list gets that list, exactly as on a quotation: 3 × 8 + 2 × 90.
        var listed = await PriceAsync(garage);
        listed.Total.ShouldBe(204m);
        ShouldMatch(listed, await QuoteAsync(garage));

        // An unknown customer is refused rather than silently priced as a guest.
        await Should.ThrowAsync<NotFoundException>(() => PriceAsync(Guid.NewGuid()));
    }
}

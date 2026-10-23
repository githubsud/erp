using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>
/// Goods sold by the piece or by the box (each item its own box size, price and barcode): stock is always counted in pieces, a box
/// has its own price (also in customer price lists), and the till sells boxes like the invoice does.
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class BoxSalesTests(ErpFixture fixture)
{
    private static async Task<decimal> OnHandAsync(HttpClient c, Guid item) =>
        (await c.GetAsync<List<JsonElement>>("/api/inventory/stock")).Single(r => r.GetProperty("itemId").GetGuid() == item).GetProperty("quantity").GetDecimal();

    [Fact]
    public async Task Selling_by_the_box_or_the_piece_counts_stock_in_pieces_at_the_box_price()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var accounts = await c.GetAsync<List<FinanceTests.Account>>("/api/finance/accounts");
        Guid A(string code) => accounts.Single(a => a.Code == code).Id;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var box = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "BOX12", nameEn = "Box of 12", nameAr = "علبة 12" })).Id;
        var carton = (await c.PostAsync<InventoryTests.Master>("/api/inventory/uoms", new { code = "CTN48", nameEn = "Carton of 48", nameAr = "كرتون 48" })).Id;
        var plug = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "NGK-BKR6E", nameEn = "Spark plug", nameAr = "بوجي", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 10m, barcode = "0087295000066"
        })).Id;
        var belt = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "BELT", nameEn = "Belt", nameAr = "سير", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 70m
        })).Id;

        // A box of 12 sells for 100 (12 pieces would be 120) and has its own barcode; the carton has no price of its own.
        var units = await c.PutAsync<List<JsonElement>>($"/api/inventory/items/{plug}/units", new object[]
        {
            new { uomId = box, factor = 12m, salesPrice = 100m, barcode = "BOX-0087295" },
            new { uomId = carton, factor = 48m }
        });
        units.Single(u => u.GetProperty("uomId").GetGuid() == box).GetProperty("salesPrice").GetDecimal().ShouldBe(100m);
        // A barcode belongs to one thing only: the piece's own barcode, or another item's box, is refused.
        (await (await c.PutJsonAsync($"/api/inventory/items/{belt}/units", new[] { new { uomId = box, factor = 12m, barcode = "BOX-0087295" } }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.barcode_taken");
        (await (await c.PutJsonAsync($"/api/inventory/items/{plug}/units", new[] { new { uomId = box, factor = 12m, barcode = "0087295000066" } }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("inv.barcode_taken");
        // Scanning the box barcode in the item search finds the item.
        (await c.GetAsync<JsonElement>("/api/inventory/items?search=BOX-0087295")).GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(plug);

        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = FinanceTests.Today, reason = "Opening", lines = new[] { new { itemId = plug, quantity = 100m, unitCost = 6m } }
        })).EnsureSuccessStatusCode();

        // The till's catalogue carries the box sizes, prices and barcodes; the carton is priced as 48 pieces.
        var catalog = (await c.GetAsync<List<JsonElement>>("/api/sales/pos/catalog")).Single(i => i.GetProperty("id").GetGuid() == plug);
        var catalogUnits = catalog.GetProperty("units").EnumerateArray().ToList();
        catalogUnits.Single(u => u.GetProperty("uomCode").GetString() == "BOX12").GetProperty("salesPrice").GetDecimal().ShouldBe(100m);
        catalogUnits.Single(u => u.GetProperty("uomCode").GetString() == "BOX12").GetProperty("barcode").GetString().ShouldBe("BOX-0087295");
        catalogUnits.Single(u => u.GetProperty("uomCode").GetString() == "CTN48").GetProperty("salesPrice").GetDecimal().ShouldBe(480m);

        // At the till: one box at the box price (not a discount, so no manager needed even for a cashier), then 2 loose pieces.
        var register = await c.PostAsync<Guid>("/api/sales/pos/registers", new
        {
            code = "T1", nameEn = "Till 1", nameAr = "صندوق 1", warehouseId = main, cashAccountId = A("1110"), cardAccountId = A("1115")
        });
        await c.PostAsync<JsonElement>("/api/core/users", new
        {
            email = $"cashier@{t.Code}.qa", displayNameEn = "Cashier", password = "User-Password-123", preferredLanguage = "en", roles = new[] { "salesperson" }
        });
        var cashier = await fixture.LoginAsync(t.Code, $"cashier@{t.Code}.qa", "User-Password-123");
        var shift = (await cashier.PostAsync<JsonElement>("/api/sales/pos/shifts", new { registerId = register, openingFloat = 0m })).GetProperty("id").GetGuid();
        var boxSale = await cashier.PostAsync<JsonElement>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift, lines = new[] { new { itemId = plug, quantity = 1m, uomId = (Guid?)box } }, cash = 100m, card = 0m
        });
        boxSale.GetProperty("total").GetDecimal().ShouldBe(100m);
        var line = boxSale.GetProperty("lines")[0];
        line.GetProperty("quantity").GetDecimal().ShouldBe(12m);
        line.GetProperty("uomCode").GetString().ShouldBe("BOX12");
        line.GetProperty("uomQuantity").GetDecimal().ShouldBe(1m);
        line.GetProperty("uomPrice").GetDecimal().ShouldBe(100m);
        (await OnHandAsync(c, plug)).ShouldBe(88m);
        (await cashier.PostAsync<JsonElement>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift, lines = new[] { new { itemId = plug, quantity = 2m, uomId = (Guid?)null } }, cash = 20m, card = 0m
        })).GetProperty("total").GetDecimal().ShouldBe(20m);
        (await OnHandAsync(c, plug)).ShouldBe(86m);
        // A box sold for less than the cashier limit allows off the box price still needs a manager.
        (await (await cashier.PostJsonAsync("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift, lines = new[] { new { itemId = plug, quantity = 1m, uomId = (Guid?)box, unitPrice = 85m } },
            cash = 85m, card = 0m
        })).ProblemAsync()).GetProperty("code").GetString().ShouldBe("sal.pos_needs_approval");

        // Part of a box comes back: 3 pieces return to stock and 3/12 of the box price is paid back.
        var refund = await cashier.PostAsync<JsonElement>("/api/sales/pos/refunds", new
        {
            clientId = Guid.NewGuid(), shiftId = shift, saleId = boxSale.GetProperty("id").GetGuid(),
            lines = new[] { new { saleLineId = line.GetProperty("id").GetGuid(), quantity = 3m } }
        });
        refund.GetProperty("total").GetDecimal().ShouldBe(-25m);
        (await OnHandAsync(c, plug)).ShouldBe(89m);

        // Invoices: a walk-in customer pays the box price; a garage on a price list pays its own box price, and a list with only a
        // piece price prices the box as 12 of those.
        var walkIn = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "WALK", nameEn = "Walk-in", nameAr = "عميل نقدي" })).GetProperty("id").GetGuid();
        var garages = (await c.PostAsync<JsonElement>("/api/sales/price-lists", new
        {
            code = "GARAGE", nameEn = "Garages", nameAr = "الكراجات", isActive = true,
            items = new object[] { new { itemId = plug, unitPrice = 8m }, new { itemId = plug, unitPrice = 90m, uomId = box } }
        }));
        garages.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("uomCode").ValueKind == JsonValueKind.Null ? "PCS" : i.GetProperty("uomCode").GetString())
            .ShouldBe(["PCS", "BOX12"]);
        var pieceOnly = (await c.PostAsync<JsonElement>("/api/sales/price-lists", new
        {
            code = "FLEET", nameEn = "Fleets", nameAr = "الأساطيل", isActive = true, items = new[] { new { itemId = plug, unitPrice = 9m } }
        })).GetProperty("id").GetGuid();
        // The same unit twice in one list is refused.
        (await c.PostJsonAsync("/api/sales/price-lists", new
        {
            code = "DUP", nameEn = "Dup", nameAr = "مكرر", isActive = true,
            items = new[] { new { itemId = plug, unitPrice = 90m, uomId = box }, new { itemId = plug, unitPrice = 91m, uomId = box } }
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var garage = (await c.PostAsync<JsonElement>("/api/sales/customers", new
        {
            code = "GAR", nameEn = "Garage", nameAr = "كراج", priceListId = garages.GetProperty("id").GetGuid()
        })).GetProperty("id").GetGuid();
        var fleet = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "FLT", nameEn = "Fleet", nameAr = "أسطول", priceListId = pieceOnly }))
            .GetProperty("id").GetGuid();

        async Task<decimal> BoxPriceFor(Guid customer)
        {
            var prices = await c.GetAsync<List<JsonElement>>($"/api/sales/prices?customerId={customer}&itemIds={plug}:{box},{plug}");
            return prices.Single(p => p.GetProperty("uomId").ValueKind != JsonValueKind.Null).GetProperty("unitPrice").GetDecimal();
        }
        (await BoxPriceFor(walkIn)).ShouldBe(100m);
        (await BoxPriceFor(garage)).ShouldBe(90m);
        (await BoxPriceFor(fleet)).ShouldBe(108m);

        var invoice = await c.PostAsync<JsonElement>("/api/sales/invoices", new
        {
            customerId = garage, invoiceDate = FinanceTests.Today, warehouseId = main,
            lines = new object[] { new { itemId = plug, quantity = 2m, uomId = box }, new { itemId = plug, quantity = 5m } }
        });
        invoice.GetProperty("total").GetDecimal().ShouldBe(2 * 90m + 5 * 8m);
        await c.PostAsync<JsonElement>($"/api/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
        (await OnHandAsync(c, plug)).ShouldBe(89m - 24m - 5m);

        // A box on the garage's account at the till becomes an invoice line by the box.
        var onAccount = await cashier.PostAsync<JsonElement>("/api/sales/pos/sales", new
        {
            clientId = Guid.NewGuid(), shiftId = shift, customerId = garage, cash = 0m, card = 0m,
            lines = new[] { new { itemId = plug, quantity = 1m, uomId = (Guid?)box, unitPrice = (decimal?)90m } }
        });
        onAccount.GetProperty("total").GetDecimal().ShouldBe(90m);
        (await OnHandAsync(c, plug)).ShouldBe(48m);
    }
}

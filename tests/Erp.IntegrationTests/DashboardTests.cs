using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class DashboardTests(ErpFixture fixture)
{
    [Fact]
    public async Task Each_role_sees_the_dashboard_figures_of_its_own_work()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await FinanceTests.PostSimpleVoucherAsync(c, 100m);
        var owner = await c.GetAsync<List<JsonElement>>("/api/core/dashboard");
        var sections = owner.Select(x => x.GetProperty("section").GetString()).Distinct().ToList();
        sections.ShouldBe(["finance", "sales", "purchasing", "inventory"], ignoreOrder: true);
        owner.Single(x => x.GetProperty("key").GetString() == "fin.cash").GetProperty("format").GetString().ShouldBe("money");

        (await c.PostJsonAsync("/api/core/users", new
        {
            email = "store@t.qa", displayNameEn = "Store", password = "User-Password-123", preferredLanguage = "en", roles = new[] { "storekeeper" }
        })).EnsureSuccessStatusCode();
        var store = await fixture.LoginAsync(t.Code, "store@t.qa", "User-Password-123");
        var mine = await store.GetAsync<List<JsonElement>>("/api/core/dashboard");
        mine.Select(x => x.GetProperty("section").GetString()).Distinct().ShouldBe(["inventory"]);
    }
}

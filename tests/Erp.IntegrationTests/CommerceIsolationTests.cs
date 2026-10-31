using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;
using Npgsql;

namespace Erp.IntegrationTests;

/// <summary>
/// Release gate (design §3, blueprint §7.4): two stores with the same product addresses; store A's slug, domain or a signed-in user of
/// either company never reads store B's data, including by guessing B's ids, and row-level security holds without the application.
/// </summary>
[Collection(ErpCollection.Name)]
public sealed class CommerceIsolationTests(ErpFixture fixture)
{
    private sealed record Shop(TenantSession Tenant, string Slug, string Host, Guid ItemId, Guid ListingId, Guid CategoryId, Guid SecretItemId);

    private async Task<Shop> ShopAsync(decimal price, string title)
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        var (main, group, pcs) = await InventoryTests.DefaultsAsync(c);
        var item = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "PLUG", nameEn = title, nameAr = title, itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = price
        })).Id;
        // A product the company sells but has not published: no slug of any store may reveal it.
        var secret = (await c.PostAsync<InventoryTests.Item>("/api/inventory/items", new
        {
            sku = "SECRET", nameEn = $"{title} unreleased", nameAr = "غير منشور", itemType = "Stock", itemGroupId = group, uomId = pcs, salesPrice = 1m
        })).Id;
        (await c.PostJsonAsync("/api/inventory/adjustments", new
        {
            warehouseId = main, date = FinanceTests.Today, reason = "Opening", lines = new[] { new { itemId = item, quantity = 10m, unitCost = 1m } }
        })).EnsureSuccessStatusCode();
        var slug = t.Code + "-store";
        var host = $"{t.Code}.shop.qa";
        await c.PutAsync<JsonElement>("/api/commerce/store", CommerceAdminTests.Store(slug, host: host));
        var category = (await c.PostAsync<JsonElement>("/api/commerce/categories", new { slug = "parts", nameEn = "Parts", nameAr = "قطع" })).GetProperty("id").GetGuid();
        var listing = (await c.PostAsync<JsonElement>("/api/commerce/listings", new
        {
            itemId = item, slug = "plug", titleEn = title, titleAr = title, categoryId = category, isPublished = true
        })).GetProperty("id").GetGuid();
        await c.PostAsync<JsonElement>("/api/commerce/listings", new { itemId = secret, slug = "secret", isPublished = false });
        return new Shop(t, slug, host, item, listing, category, secret);
    }

    private static async Task<string> GetAsync(HttpClient client, string url, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (host is not null) request.Headers.Host = host;
        var response = await client.SendAsync(request);
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : $"<{(int)response.StatusCode}>";
    }

    [Fact]
    public async Task A_store_slug_never_reads_another_tenants_store()
    {
        var a = await ShopAsync(10m, "Alpha plug");
        var b = await ShopAsync(99m, "Bravo plug");
        var shopper = fixture.Factory.CreateClient();

        // Each slug and each domain shows its own store only; the same product address resolves within the store.
        foreach (var (mine, theirs) in new[] { (a, b), (b, a) })
        {
            var title = mine == a ? "Alpha" : "Bravo";
            var other = mine == a ? "Bravo" : "Alpha";
            foreach (var url in new[] { "", "/catalog", "/categories", "/products/plug", "/catalog?category=parts", "/catalog?search=plug" })
            {
                var bySlug = await GetAsync(shopper, $"/api/store/{mine.Slug}{url}");
                var byHost = await GetAsync(shopper, $"/api/store/_{url}", mine.Host);
                foreach (var body in new[] { bySlug, byHost })
                {
                    body.ShouldNotStartWith("<");
                    body.ShouldNotContain(other);
                    foreach (var id in new[] { theirs.Tenant.TenantId, theirs.Tenant.CompanyId, theirs.ItemId, theirs.ListingId, theirs.CategoryId })
                        body.ShouldNotContain(id.ToString());
                }
                if (url == "/products/plug") bySlug.ShouldContain(title);
            }
            // The slug decides, whatever host the request names.
            (await GetAsync(shopper, $"/api/store/{mine.Slug}/products/plug", theirs.Host)).ShouldContain(title);
        }
        (await GetAsync(shopper, $"/api/store/{a.Slug}/products/plug")).ShouldContain("\"price\":10");
        (await GetAsync(shopper, $"/api/store/{b.Slug}/products/plug")).ShouldContain("\"price\":99");

        // Guessing: B's ids, unpublished products and odd slugs through A's store never answer with data.
        foreach (var guess in new[]
                 {
                     $"/products/{b.ListingId}", $"/products/{b.ItemId}", $"/products/{b.SecretItemId}", "/products/secret", $"/catalog?category={b.CategoryId}",
                     $"/catalog?search={b.Tenant.Code}", "/products/..%2F..%2Fcatalog", "/products/PLUG"
                 })
        {
            var body = await GetAsync(shopper, $"/api/store/{a.Slug}{guess}");
            body.ShouldNotContain("Bravo");
            body.ShouldNotContain("unreleased");
        }
        foreach (var slug in new[] { b.Tenant.TenantId.ToString(), b.Tenant.Code, b.Slug.ToUpperInvariant() + "%20", $"{a.Slug}%2F..%2F{b.Slug}", "", "%5F" })
            (await GetAsync(shopper, $"/api/store/{slug}/catalog")).ShouldNotContain("Alpha");

        // A signed-in user of company B browsing store A gets store A's public data, never B's.
        var staffB = await GetAsync(b.Tenant.Client, $"/api/store/{a.Slug}/catalog");
        staffB.ShouldContain("Alpha");
        staffB.ShouldNotContain("Bravo");
        // And A's staff cannot reach B's records through the admin API by id.
        var adminA = a.Tenant.Client;
        (await adminA.PutJsonAsync($"/api/commerce/listings/{b.ListingId}", new { itemId = b.ItemId, isPublished = false })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PutJsonAsync($"/api/commerce/categories/{b.CategoryId}", new { slug = "x", nameEn = "X", nameAr = "س" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PostJsonAsync("/api/commerce/listings", new { itemId = b.SecretItemId, isPublished = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PostJsonAsync("/api/commerce/categories", new { slug = "y", nameEn = "Y", nameAr = "ص", parentId = b.CategoryId })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PutJsonAsync($"/api/commerce/listings/{a.ListingId}", new { itemId = a.ItemId, categoryId = b.CategoryId, isPublished = true }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PutJsonAsync("/api/commerce/store", CommerceAdminTests.Store(b.Slug))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await adminA.GetStringAsync("/api/commerce/listings")).ShouldNotContain("Bravo");
        (await GetAsync(shopper, $"/api/store/{b.Slug}/products/plug")).ShouldContain("Bravo");
    }

    [Fact]
    public async Task Row_level_security_keeps_commerce_tables_per_tenant_and_the_directory_holds_only_keys()
    {
        var a = await ShopAsync(10m, "Alpha plug");
        var b = await ShopAsync(99m, "Bravo plug");

        async Task<long> CountAsync(NpgsqlConnection connection, string sql) => (long)(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync())!;
        await using (var none = await fixture.OpenAppConnectionAsync(null))
            foreach (var table in new[] { "store", "category", "listing", "listing_image" })
                (await CountAsync(none, $"SELECT count(*) FROM commerce.{table}")).ShouldBe(0);
        await using (var asA = await fixture.OpenAppConnectionAsync(a.Tenant.TenantId))
        {
            foreach (var table in new[] { "store", "category", "listing" })
            {
                (await CountAsync(asA, $"SELECT count(*) FROM commerce.{table} WHERE tenant_id = '{b.Tenant.TenantId}'")).ShouldBe(0);
                (await CountAsync(asA, $"SELECT count(*) FROM commerce.{table}")).ShouldBeGreaterThan(0);
            }
            (await CountAsync(asA, $"SELECT count(*) FROM commerce.listing WHERE id = '{b.ListingId}'")).ShouldBe(0);
            // Writing into B's tenant is refused by the policy.
            await Should.ThrowAsync<PostgresException>(() => new NpgsqlCommand(
                $"INSERT INTO commerce.category (id, tenant_id, company_id, store_id, slug, name_en, name_ar, created_by) " +
                $"SELECT gen_random_uuid(), '{b.Tenant.TenantId}', company_id, id, 'evil', 'x', 'x', gen_random_uuid() FROM commerce.store", asA).ExecuteNonQueryAsync());
            await new NpgsqlCommand($"UPDATE commerce.listing SET title_en = 'hacked' WHERE id = '{b.ListingId}'", asA).ExecuteNonQueryAsync();
        }
        (await GetAsync(fixture.Factory.CreateClient(), $"/api/store/{b.Slug}/products/plug")).ShouldNotContain("hacked");

        // The directory is readable without a tenant (that is its job) but has no names, prices or products in it.
        await using var anyone = await fixture.OpenAppConnectionAsync(null);
        var columns = new List<string>();
        await using (var reader = await new NpgsqlCommand(
                         "SELECT column_name FROM information_schema.columns WHERE table_schema = 'commerce' AND table_name = 'store_domain'", anyone).ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        columns.ShouldBe(["store_slug", "host", "tenant_id", "company_id", "store_id", "is_active", "created_at", "updated_at"], ignoreOrder: true);
    }
}

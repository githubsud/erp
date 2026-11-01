using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

/// <summary>Product photos: files on disk named by a row of the company's store; shoppers see them only for published products.</summary>
[Collection(ErpCollection.Name)]
public sealed class CommercePhotoTests(ErpFixture fixture)
{
    /// <summary>The smallest JPEG header the format check accepts, plus some bytes to tell photos apart.</summary>
    private static byte[] Jpeg(byte mark) => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, mark, mark, 0xFF, 0xD9];

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, Guid listing, byte[] bytes, string name = "photo.jpg")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return c.PostAsync($"/api/commerce/listings/{listing}/images", new MultipartFormDataContent { { content, "file", name } });
    }

    private static async Task<Guid> ListingAsync(HttpClient c) =>
        (await c.GetAsync<List<JsonElement>>("/api/commerce/listings")).Single().GetProperty("id").GetGuid();

    [Fact]
    public async Task Photos_are_added_ordered_shown_to_shoppers_and_deleted()
    {
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        var c = s.Tenant.Client;
        var listing = await ListingAsync(c);

        var first = await (await UploadAsync(c, listing, Jpeg(1))).ReadAsync<JsonElement>();
        var second = await (await UploadAsync(c, listing, Png(), "second.png")).ReadAsync<JsonElement>();
        var firstId = first.GetProperty("id").GetGuid();
        var secondId = second.GetProperty("id").GetGuid();
        (await c.GetAsync<List<JsonElement>>("/api/commerce/listings")).Single().GetProperty("images").EnumerateArray().Select(i => i.GetGuid())
            .ShouldBe([firstId, secondId]);

        // The shopper gets photo addresses on the product, in order, and the photo itself, cacheable for good.
        var shopper = fixture.Factory.CreateClient();
        var product = await shopper.GetAsync<JsonElement>($"/api/store/{s.Slug}/products/plug");
        var urls = product.GetProperty("images").EnumerateArray().Select(i => i.GetString()!).ToList();
        urls.ShouldBe([$"/api/store/{s.Slug}/images/{firstId}", $"/api/store/{s.Slug}/images/{secondId}"]);
        var photo = await shopper.GetAsync(urls[0]);
        photo.StatusCode.ShouldBe(HttpStatusCode.OK);
        photo.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        photo.Headers.CacheControl!.Public.ShouldBeTrue();
        (await photo.Content.ReadAsByteArrayAsync()).ShouldBe(Jpeg(1));
        (await shopper.GetAsync(urls[1])).Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await shopper.GetAsync<JsonElement>($"/api/store/{s.Slug}/catalog")).GetProperty("items")[0].GetProperty("images").GetArrayLength().ShouldBe(2);

        // The PNG first.
        (await c.PutJsonAsync($"/api/commerce/listings/{listing}/images/order", new { ids = new[] { secondId, firstId } })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await shopper.GetAsync<JsonElement>($"/api/store/{s.Slug}/products/plug")).GetProperty("images")[0].GetString().ShouldEndWith(secondId.ToString());
        (await (await c.PutJsonAsync($"/api/commerce/listings/{listing}/images/order", new { ids = new[] { secondId } })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("commerce.photo_order");

        // Back office sees its photos; the file goes when the photo is deleted.
        (await c.GetAsync($"/api/commerce/images/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.GetFiles(fixture.FilesRoot, $"{firstId:N}.jpg", SearchOption.AllDirectories).Length.ShouldBe(1);
        (await c.DeleteAsync($"/api/commerce/listings/{listing}/images/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.GetFiles(fixture.FilesRoot, $"{firstId:N}.jpg", SearchOption.AllDirectories).ShouldBeEmpty();
        (await shopper.GetAsync(urls[0])).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.GetAsync($"/api/commerce/images/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A hidden product's photos are not public, but the back office still has them.
        await c.PostAsync<JsonElement>("/api/commerce/listings/publish", new { itemIds = new[] { s.Plug }, publish = false });
        (await shopper.GetAsync(urls[1])).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.GetAsync($"/api/commerce/images/{secondId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_small_jpeg_png_or_webp_photos_are_taken_and_at_most_eight_per_product()
    {
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        var c = s.Tenant.Client;
        var listing = await ListingAsync(c);

        // The name says JPEG, the bytes say HTML: refused.
        (await (await UploadAsync(c, listing, "<html><script>alert(1)</script></html>"u8.ToArray())).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("commerce.photo_type");
        (await (await UploadAsync(c, listing, [])).ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.photo_size");
        var big = new byte[2 * 1024 * 1024 + 1];
        Jpeg(0).CopyTo(big, 0);
        (await (await UploadAsync(c, listing, big)).ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.photo_size");

        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        (await UploadAsync(c, listing, webp, "x.webp")).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Several at once still stop at eight.
        var results = await Task.WhenAll(Enumerable.Range(1, 9).Select(i => UploadAsync(c, listing, Jpeg((byte)i))));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(7);
        (await results.First(r => r.StatusCode != HttpStatusCode.OK).ProblemAsync()).GetProperty("code").GetString().ShouldBe("commerce.photo_limit");
        (await c.GetAsync<List<JsonElement>>($"/api/commerce/listings/{listing}/images")).Count.ShouldBe(8);
    }

    [Fact]
    public async Task Another_company_cannot_see_or_change_the_photos()
    {
        var s = await CommerceCartTests.ReadyStoreAsync(fixture);
        var listing = await ListingAsync(s.Tenant.Client);
        var photo = (await (await UploadAsync(s.Tenant.Client, listing, Jpeg(7))).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();

        var other = await CommerceCartTests.ReadyStoreAsync(fixture);
        var c = other.Tenant.Client;
        (await c.GetAsync($"/api/commerce/images/{photo}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.GetAsync($"/api/commerce/listings/{listing}/images")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(c, listing, Jpeg(8))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.DeleteAsync($"/api/commerce/listings/{listing}/images/{photo}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Nor through its own store's public address.
        (await fixture.Factory.CreateClient().GetAsync($"/api/store/{other.Slug}/images/{photo}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.Factory.CreateClient().GetAsync($"/api/store/{s.Slug}/images/{photo}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Without the products permission (a salesperson), not even its own company's photos.
        var t = s.Tenant;
        await t.Client.PostAsync<JsonElement>("/api/core/users", new
        {
            email = $"seller@{t.Code}.qa", displayNameEn = "Seller", password = "User-Password-123", preferredLanguage = "en", roles = new[] { "salesperson" }
        });
        var cashier = await fixture.LoginAsync(t.Code, $"seller@{t.Code}.qa", "User-Password-123");
        (await cashier.GetAsync($"/api/commerce/images/{photo}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await UploadAsync(cashier, listing, Jpeg(9))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

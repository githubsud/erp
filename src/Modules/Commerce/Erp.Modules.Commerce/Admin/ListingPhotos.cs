using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Commerce.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Commerce.Admin;

public sealed record ListingImageDto(Guid Id, int SortOrder);

public sealed record ImageOrderRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Product photos, kept as files (<see cref="IFileStore"/>) named by a row in commerce.listing_image. The browser makes photos smaller
/// before sending them; here only the size and the format (by the file's first bytes, not its name) are checked. The first photo
/// is the one shoppers see first.
/// </summary>
internal sealed class ListingPhotos(ErpDbContext db, ITenantContext tenant, IFileStore files, StoreAdminService admin, ILogger<ListingPhotos> log)
{
    public const int MaxPerListing = 8;
    public const long MaxBytes = 2 * 1024 * 1024;

    public async Task<IReadOnlyList<ListingImage>> ListAsync(Guid listingId, CancellationToken ct)
    {
        await ListingAsync(listingId, ct);
        return await db.Set<ListingImage>().AsNoTracking().Where(i => i.ListingId == listingId).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync(ct);
    }

    public async Task<ListingImage> AddAsync(Guid listingId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || file.Length > MaxBytes)
            throw new BusinessRuleException("commerce.photo_size", "Choose a photo smaller than 2 MB.", "اختر صورة أصغر من 2 ميغابايت.");
        using var buffer = new MemoryStream((int)file.Length);
        await using (var upload = file.OpenReadStream()) await upload.CopyToAsync(buffer, ct);
        var extension = PhotoFormat.Detect(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
                        ?? throw new BusinessRuleException("commerce.photo_type", "Use a JPEG, PNG or WebP photo.", "استخدم صورة بصيغة JPEG أو PNG أو WebP.");

        return await db.InTransactionAsync(async token =>
        {
            var listing = await ListingAsync(listingId, token);
            // One upload at a time per product, so the limit holds when several arrive together.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM commerce.listing WHERE tenant_id = {listing.TenantId} AND id = {listing.Id} FOR UPDATE", token);
            var orders = await db.Set<ListingImage>().Where(i => i.ListingId == listing.Id).Select(i => i.SortOrder).ToListAsync(token);
            if (orders.Count >= MaxPerListing)
                throw new BusinessRuleException("commerce.photo_limit", $"A product can have up to {MaxPerListing} photos.",
                    $"يمكن إضافة {MaxPerListing} صور كحد أقصى للمنتج.");

            var image = new ListingImage { ListingId = listing.Id, StorageKey = "", SortOrder = orders.Count == 0 ? 0 : orders.Max() + 1 };
            image.StorageKey = $"{tenant.RequireTenantId():N}/commerce/{image.Id:N}{extension}";
            buffer.Position = 0;
            await files.SaveAsync(image.StorageKey, buffer, token);
            db.Add(image);
            try
            {
                await db.SaveChangesAsync(token);
            }
            catch
            {
                await files.DeleteAsync(image.StorageKey, CancellationToken.None);
                throw;
            }
            return image;
        }, ct);
    }

    /// <summary>Puts the photos in the given order (all of them, each once); the first is shown first.</summary>
    public Task OrderAsync(Guid listingId, ImageOrderRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await ListingAsync(listingId, token);
            var images = await db.Set<ListingImage>().Where(i => i.ListingId == listingId).ToListAsync(token);
            var ids = r.Ids ?? [];
            if (ids.Count != images.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => images.All(i => i.Id != id)))
                throw new BusinessRuleException("commerce.photo_order", "The photo list has changed; reload and try again.",
                    "تغيّرت قائمة الصور؛ أعد التحميل وحاول مرة أخرى.");
            foreach (var image in images) image.SortOrder = ids.ToList().IndexOf(image.Id);
            await db.SaveChangesAsync(token);
            return true;
        }, ct);

    public async Task DeleteAsync(Guid listingId, Guid imageId, CancellationToken ct)
    {
        var key = await db.InTransactionAsync(async token =>
        {
            await ListingAsync(listingId, token);
            var image = await db.Set<ListingImage>().SingleOrDefaultAsync(i => i.Id == imageId && i.ListingId == listingId, token)
                        ?? throw new NotFoundException("Photo", imageId);
            db.Remove(image);
            await db.SaveChangesAsync(token);
            return image.StorageKey;
        }, ct);
        // The row is gone, so nobody can reach the file any more; removing it is tidying up.
        try
        {
            await files.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not delete photo file {Key}", key);
        }
    }

    /// <summary>The storage key of one of this company's photos (for the back office, published or not).</summary>
    public async Task<string> KeyAsync(Guid imageId, CancellationToken ct)
    {
        var store = await admin.StoreAsync(ct);
        return await (from i in db.Set<ListingImage>()
                      join l in db.Set<Listing>() on i.ListingId equals l.Id
                      where i.Id == imageId && l.StoreId == store.Id
                      select i.StorageKey).SingleOrDefaultAsync(ct)
               ?? throw new NotFoundException("Photo", imageId);
    }

    private async Task<Listing> ListingAsync(Guid listingId, CancellationToken ct)
    {
        var store = await admin.StoreAsync(ct);
        return await db.Set<Listing>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == listingId && l.StoreId == store.Id, ct)
               ?? throw new NotFoundException("Listing", listingId);
    }
}

/// <summary>Recognises the photo formats the store accepts by their first bytes, and serves stored photos.</summary>
internal static class PhotoFormat
{
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    public static string ContentType(string key) => Path.GetExtension(key) switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Public photos never change (a new photo gets a new id), so browsers may keep them for a year; back-office copies stay private.
    /// </summary>
    public static async Task<IResult> ServeAsync(HttpContext http, IFileStore files, string key, bool isPublic, CancellationToken ct)
    {
        var stream = await files.OpenAsync(key, ct) ?? throw new NotFoundException("Photo", Path.GetFileNameWithoutExtension(key));
        http.Response.Headers.CacheControl = isPublic ? "public, max-age=31536000, immutable" : "private, max-age=86400";
        return Results.File(stream, ContentType(key));
    }
}

internal static class ListingPhotoEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/commerce").WithTags("Commerce").RequireAuthorization();

        g.MapGet("/listings/{id:guid}/images", async (Guid id, ListingPhotos photos, CancellationToken ct) =>
            (await photos.ListAsync(id, ct)).Select(i => new ListingImageDto(i.Id, i.SortOrder))).RequirePermission(CommercePermissions.ListingEdit);
        g.MapPost("/listings/{id:guid}/images", async (Guid id, IFormFile? file, ListingPhotos photos, CancellationToken ct) =>
        {
            var image = await photos.AddAsync(id, file, ct);
            return new ListingImageDto(image.Id, image.SortOrder);
        }).RequirePermission(CommercePermissions.ListingEdit).DisableAntiforgery();
        g.MapPut("/listings/{id:guid}/images/order", async (Guid id, ImageOrderRequest r, ListingPhotos photos, CancellationToken ct) =>
        {
            await photos.OrderAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(CommercePermissions.ListingEdit);
        g.MapDelete("/listings/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId, ListingPhotos photos, CancellationToken ct) =>
        {
            await photos.DeleteAsync(id, imageId, ct);
            return Results.NoContent();
        }).RequirePermission(CommercePermissions.ListingEdit);
        g.MapGet("/images/{imageId:guid}", async (Guid imageId, HttpContext http, ListingPhotos photos, IFileStore files, CancellationToken ct) =>
            await PhotoFormat.ServeAsync(http, files, await photos.KeyAsync(imageId, ct), false, ct)).RequirePermission(CommercePermissions.ListingEdit);
    }
}

using System.Text.RegularExpressions;
using Erp.Modules.Commerce.Domain;
using Erp.Modules.Inventory.Contracts;

namespace Erp.Modules.Commerce.Storefront;

/// <summary>
/// Shopper search, with the till's rules (core/part-search.ts): every word must match the product's titles, code, brand, a barcode,
/// a part number ("04152-YZZA1" = "04152yzza1"), or a car it fits ("corolla 2015 filter"). A model year belongs to one car: the
/// part must fit, in that year, a car that also answers the other car words.
/// </summary>
internal static partial class StorefrontSearch
{
    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex NotLetterOrDigit();

    public static string PartKey(string text) => NotLetterOrDigit().Replace(text, "").ToUpperInvariant();

    public static bool Matches(Listing listing, CatalogItem item, string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fits = item.Fits ?? [];
        var carWords = words.Where(w => YearOf(w) is null && fits.Any(f => CarHas(f, w.ToLowerInvariant()))).Select(w => w.ToLowerInvariant()).ToList();
        return words.All(word =>
        {
            var w = word.ToLowerInvariant();
            var key = PartKey(word);
            var year = YearOf(word);
            return listing.TitleEn.Contains(w, StringComparison.OrdinalIgnoreCase)
                   || listing.TitleAr.Contains(word, StringComparison.Ordinal)
                   || item.Sku.Contains(w, StringComparison.OrdinalIgnoreCase)
                   || item.NameEn.Contains(w, StringComparison.OrdinalIgnoreCase)
                   || item.NameAr.Contains(word, StringComparison.Ordinal)
                   || item.Barcode == word
                   || (item.Brand?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (item.Units ?? []).Any(u => u.Barcode == word)
                   || (key != "" && (item.PartKeys ?? []).Any(k => k.Contains(key, StringComparison.Ordinal)))
                   || (year is { } y && fits.Any(f => FitsYear(f, y) && carWords.All(c => CarHas(f, c))))
                   || fits.Any(f => CarHas(f, w));
        });
    }

    private static int? YearOf(string word) => word.Length == 4 && int.TryParse(word, out var y) && y is >= 1950 and <= 2100 ? y : null;

    private static bool FitsYear(CatalogFitment f, int year) => (f.YearFrom is null || f.YearFrom <= year) && (f.YearTo is null || f.YearTo >= year);

    private static bool CarHas(CatalogFitment f, string w) =>
        f.Make.Contains(w, StringComparison.OrdinalIgnoreCase) || f.Model.Contains(w, StringComparison.OrdinalIgnoreCase)
        || (f.Engine?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false);
}

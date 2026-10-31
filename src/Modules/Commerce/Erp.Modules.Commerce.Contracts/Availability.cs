namespace Erp.Modules.Commerce.Contracts;

/// <summary>
/// What the online store shows instead of a quantity (exact stock is never public): out of stock at zero or below, low at or below the
/// item's reorder level, otherwise in stock. Services are always in stock.
/// </summary>
public enum AvailabilityBand { InStock, Low, OutOfStock }

public static class Availability
{
    public static AvailabilityBand Band(decimal? onHand, decimal? reorderLevel, bool tracksStock)
    {
        if (!tracksStock) return AvailabilityBand.InStock;
        var quantity = onHand ?? 0m;
        if (quantity <= 0) return AvailabilityBand.OutOfStock;
        return reorderLevel is { } level && quantity <= level ? AvailabilityBand.Low : AvailabilityBand.InStock;
    }
}

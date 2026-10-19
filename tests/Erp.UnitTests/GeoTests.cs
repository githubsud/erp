using Erp.Modules.HR.SelfService;

namespace Erp.UnitTests;

public sealed class GeoTests
{
    [Fact]
    public void Distance_matches_known_points_in_doha()
    {
        // 0.01° of latitude ≈ 1,112 m anywhere.
        Geo.DistanceM(25.2854, 51.5310, 25.2954, 51.5310).ShouldBe(1_112, 2);
        Geo.DistanceM(25.2854, 51.5310, 25.2854, 51.5310).ShouldBe(0);
        // Corniche to Hamad International Airport, roughly 8 km.
        Geo.DistanceM(25.2950, 51.5310, 25.2731, 51.6081).ShouldBeInRange(7_500, 8_300);
    }
}

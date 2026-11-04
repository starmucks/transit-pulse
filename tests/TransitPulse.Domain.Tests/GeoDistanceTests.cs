namespace TransitPulse.Domain.Tests;

public sealed class GeoDistanceTests
{
    [Fact]
    public void Same_point_is_zero_metres()
    {
        Assert.Equal(0, GeoDistance.Metres(60.17, 24.94, 60.17, 24.94));
    }

    [Fact]
    public void A_degree_of_latitude_is_about_111_km()
    {
        Assert.Equal(111_195, GeoDistance.Metres(60, 25, 61, 25), tolerance: 1);
    }

    [Fact]
    public void A_degree_of_longitude_at_60_degrees_north_is_half_of_that()
    {
        Assert.Equal(55_597, GeoDistance.Metres(60, 25, 60, 26), tolerance: 1);
    }

    [Fact]
    public void Distance_does_not_depend_on_the_direction()
    {
        var there = GeoDistance.Metres(60.2298, 25.0103, 60.2906, 24.8667);
        var back = GeoDistance.Metres(60.2906, 24.8667, 60.2298, 25.0103);

        Assert.Equal(there, back);
    }

    [Fact]
    public void Two_real_buses_are_as_far_apart_as_the_exact_formula_says()
    {
        // The pair of the HSL contract fixtures; the great-circle (haversine) distance is 10 413 m.
        Assert.Equal(10_413, GeoDistance.Metres(60.229776, 25.010284, 60.290588, 24.866717), tolerance: 1);
    }
}

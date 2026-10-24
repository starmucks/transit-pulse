namespace TransitPulse.Domain.Tests;

public sealed class VehiclePositionTests
{
    private static readonly DateTimeOffset _utcNow = new(2026, 10, 2, 19, 10, 52, TimeSpan.Zero);

    // Fields of a real HSL HFP sample (brief §4.1), already normalized.
    private static VehiclePosition Create(
        string feedId = "hsl",
        string vehicleId = "40/415",
        double lat = 60.202731,
        double lon = 24.924969,
        DateTimeOffset? observedAt = null,
        float? speedMps = 8.03f,
        float? bearing = 254f) =>
        new(feedId, vehicleId, "1009-1-2026-10-02-21:31", "1009", lat, lon,
            speedMps, bearing, -195, OccupancyLevel.Empty, observedAt ?? _utcNow);

    [Fact]
    public void Valid_position_keeps_all_values()
    {
        var position = Create();

        Assert.Equal("40/415", position.VehicleId);
        Assert.Equal(60.202731, position.Lat);
        Assert.Equal(24.924969, position.Lon);
        Assert.Equal(_utcNow, position.ObservedAt);
    }

    [Theory]
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    public void Coordinate_bounds_are_inclusive(double lat, double lon)
    {
        var position = Create(lat: lat, lon: lon);

        Assert.Equal(lat, position.Lat);
        Assert.Equal(lon, position.Lon);
    }

    [Theory]
    [InlineData(90.0001)]
    [InlineData(-90.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Latitude_out_of_range_is_rejected(double lat)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Create(lat: lat));
        Assert.Equal("Lat", ex.ParamName);
    }

    [Theory]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    [InlineData(24667220)] // Tallinn feed sends integer micro-degrees; an unscaled value must not slip through.
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Longitude_out_of_range_is_rejected(double lon)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Create(lon: lon));
        Assert.Equal("Lon", ex.ParamName);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Non_finite_speed_and_bearing_are_rejected(float value)
    {
        // JSON has no NaN: one such value would fail serialization of a whole batch downstream.
        Assert.Equal("SpeedMps", Assert.Throws<ArgumentOutOfRangeException>(() => Create(speedMps: value)).ParamName);
        Assert.Equal("Bearing", Assert.Throws<ArgumentOutOfRangeException>(() => Create(bearing: value)).ParamName);
    }

    [Fact]
    public void Unknown_speed_and_bearing_are_allowed()
    {
        var position = Create(speedMps: null, bearing: null);

        Assert.Null(position.SpeedMps);
        Assert.Null(position.Bearing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_ids_are_rejected(string id)
    {
        Assert.Throws<ArgumentException>(() => Create(vehicleId: id));
        Assert.Throws<ArgumentException>(() => Create(feedId: id));
    }

    [Fact]
    public void Null_ids_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Create(vehicleId: null!));
        Assert.Throws<ArgumentNullException>(() => Create(feedId: null!));
    }

    [Fact]
    public void Non_utc_time_is_rejected()
    {
        var helsinkiLocal = new DateTimeOffset(2026, 10, 2, 22, 10, 52, TimeSpan.FromHours(3));

        Assert.Throws<ArgumentException>(() => Create(observedAt: helsinkiLocal));
    }

    [Fact]
    public void Unset_time_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Create(observedAt: default(DateTimeOffset)));

    [Fact]
    public void Identical_positions_are_equal() =>
        Assert.Equal(Create(), Create());

    [Fact]
    public void With_expression_keeps_validated_values()
    {
        var delayed = Create() with { DelaySec = 300 };

        Assert.Equal(300, delayed.DelaySec);
        Assert.Equal(60.202731, delayed.Lat);
    }
}

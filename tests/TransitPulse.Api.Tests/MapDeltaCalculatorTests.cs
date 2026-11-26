using TransitPulse.Api.Map;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Tests;

public sealed class MapDeltaCalculatorTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Diff_classifies_added_updated_and_removed()
    {
        var previous = new Dictionary<string, MapVehicle>(StringComparer.Ordinal)
        {
            ["a"] = Vehicle("a", 24.9, 60.1),
            ["b"] = Vehicle("b", 24.91, 60.11),
        };
        var current = new[]
        {
            Vehicle("b", 24.92, 60.11),
            Vehicle("c", 24.93, 60.12),
        };

        var delta = MapDeltaCalculator.Diff(previous, current, generation: 7);

        Assert.Equal(["c"], delta.Added.Select(v => v.VehicleId));
        Assert.Equal(["b"], delta.Updated.Select(v => v.VehicleId));
        Assert.Equal(["a"], delta.Removed);
        Assert.Equal(7L, delta.Generation);
    }

    [Fact]
    public void Diff_is_empty_when_snapshots_match()
    {
        var vehicle = Vehicle("a", 24.9, 60.1);
        var previous = new Dictionary<string, MapVehicle>(StringComparer.Ordinal) { ["a"] = vehicle };

        var delta = MapDeltaCalculator.Diff(previous, [vehicle], generation: 1);

        Assert.Empty(delta.Added);
        Assert.Empty(delta.Updated);
        Assert.Empty(delta.Removed);
        Assert.Equal(1L, delta.Generation);
    }

    private static MapVehicle Vehicle(string id, double lon, double lat) =>
        new(id, lon, lat, null, "1009", null, null, _at);
}

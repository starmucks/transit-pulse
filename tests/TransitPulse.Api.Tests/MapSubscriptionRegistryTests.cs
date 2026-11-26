using TransitPulse.Api.Map;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Tests;

public sealed class MapSubscriptionRegistryTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly GeoBoundingBox _boxA = new(24.9, 60.1, 25.0, 60.2);
    private static readonly GeoBoundingBox _boxB = new(24.8, 60.05, 24.9, 60.15);

    [Fact]
    public void ReplaceLastSent_ignores_stale_generation_after_Subscribe()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));

        var snapshot = Assert.Single(registry.Snapshot());
        Assert.Equal(1L, snapshot.Generation);

        // Client pan/zoom: Subscribe clears last-sent and bumps generation while a tick is in flight.
        registry.Set(new MapSubscription("c1", "hsl", _boxB, RouteId: null));
        Assert.Empty(registry.GetLastSent("c1"));

        var staleVehicles = new[] { Vehicle("v1", 24.95, 60.15) };
        Assert.False(registry.ReplaceLastSent("c1", snapshot.Generation, staleVehicles));
        Assert.Empty(registry.GetLastSent("c1"));
        Assert.Equal(2L, registry.GetGeneration("c1"));
        Assert.False(registry.IsCurrent("c1", snapshot.Generation));

        var fresh = Assert.Single(registry.Snapshot());
        Assert.Equal(2L, fresh.Generation);
        Assert.True(registry.ReplaceLastSent("c1", fresh.Generation, staleVehicles));
        Assert.Contains("v1", registry.GetLastSent("c1").Keys);
    }

    [Fact]
    public void ReplaceLastSent_updates_when_generation_matches()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));
        var generation = Assert.Single(registry.Snapshot()).Generation;

        var vehicles = new[] { Vehicle("a", 24.91, 60.11), Vehicle("b", 24.92, 60.12) };
        Assert.True(registry.ReplaceLastSent("c1", generation, vehicles));

        var lastSent = registry.GetLastSent("c1");
        Assert.Equal(2, lastSent.Count);
        Assert.Contains("a", lastSent.Keys);
        Assert.Contains("b", lastSent.Keys);
    }

    [Fact]
    public void ReplaceLastSent_cas_does_not_roll_back_Subscribe()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));
        var staleGeneration = Assert.Single(registry.Snapshot()).Generation;

        // Simulate the race: tick has already read the entry (gen=1) when Subscribe bumps to gen=2.
        // A non-CAS write of (entry with LastSent) would restore generation 1; TryUpdate must fail.
        registry.Set(new MapSubscription("c1", "hsl", _boxB, RouteId: null));
        Assert.Equal(2L, registry.GetGeneration("c1"));
        Assert.Empty(registry.GetLastSent("c1"));

        Assert.False(registry.ReplaceLastSent("c1", staleGeneration, [Vehicle("stale", 24.9, 60.1)]));
        Assert.Equal(2L, registry.GetGeneration("c1"));
        Assert.Equal(_boxB, Assert.Single(registry.Snapshot()).Subscription.Box);
        Assert.Empty(registry.GetLastSent("c1"));
    }

    [Fact]
    public void IsCurrent_is_false_after_Subscribe_between_cas_and_send()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));
        var generation = Assert.Single(registry.Snapshot()).Generation;

        Assert.True(registry.ReplaceLastSent("c1", generation, [Vehicle("a", 24.91, 60.11)]));
        Assert.True(registry.IsCurrent("c1", generation));

        registry.Set(new MapSubscription("c1", "hsl", _boxB, RouteId: null));
        Assert.False(registry.IsCurrent("c1", generation));
        Assert.Empty(registry.GetLastSent("c1"));
    }

    private static MapVehicle Vehicle(string id, double lon, double lat) =>
        new(id, lon, lat, Bearing: null, RouteId: null, TripId: null, DelaySec: null, ObservedAt: _at);
}

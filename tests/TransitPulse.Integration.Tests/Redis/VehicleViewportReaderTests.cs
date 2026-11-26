using StackExchange.Redis;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Integration.Tests.Redis;

public sealed class VehicleViewportReaderTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConnectionMultiplexer? _connection;

    public async ValueTask InitializeAsync() =>
        _connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task List_returns_vehicles_inside_the_box_and_skips_expired_hashes()
    {
        const string feed = "hsl-box";
        var writer = new VehicleGeoStore(_connection!, TimeSpan.FromSeconds(120));
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        await writer.UpsertManyAsync(
            [
                Enriched(feed, "inside", 24.94, 60.17, "1009", at),
                Enriched(feed, "outside", 25.5, 60.5, "1009", at),
            ],
            Ct);

        // GEO member without a hash must be ignored.
        await _connection!.GetDatabase().GeoAddAsync(VehicleGeoStore.GeoKey(feed), 24.95, 60.18, "ghost");

        var reader = new VehicleViewportReader(_connection);
        var box = new GeoBoundingBox(24.9, 60.15, 25.0, 60.2);

        var vehicles = await reader.ListInBoxAsync(feed, box, routeId: null, limit: 50, Ct);

        Assert.Equal(["inside"], vehicles.Select(v => v.VehicleId));
        Assert.Equal("1009", vehicles[0].RouteId);
        Assert.NotNull(vehicles[0].RedisWrittenAt);
    }

    [Fact]
    public async Task List_filters_by_route_id()
    {
        const string feed = "hsl-route";
        var writer = new VehicleGeoStore(_connection!, TimeSpan.FromSeconds(120));
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        await writer.UpsertManyAsync(
            [
                Enriched(feed, "tram", 24.94, 60.17, "1009", at),
                Enriched(feed, "bus", 24.945, 60.175, "2015", at),
            ],
            Ct);

        var reader = new VehicleViewportReader(_connection!);
        var box = new GeoBoundingBox(24.9, 60.15, 25.0, 60.2);

        var vehicles = await reader.ListInBoxAsync(feed, box, routeId: "1009", limit: 50, Ct);

        Assert.Equal(["tram"], vehicles.Select(v => v.VehicleId));
    }

    private static EnrichedPosition Enriched(
        string feedId, string vehicleId, double lon, double lat, string routeId, DateTimeOffset at) =>
        new(
            new VehiclePosition(feedId, vehicleId, $"{routeId}_trip", routeId, lat, lon, null, 90f, null, null, at),
            new ScheduledProgress($"{routeId}_trip", routeId, 0, DateOnly.FromDateTime(at.UtcDateTime), 0, null),
            30);
}

using StackExchange.Redis;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Integration.Tests.Redis;

public sealed class VehicleGeoStoreTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
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
    public async Task Upsert_writes_geo_member_and_attribute_hash_with_ttl()
    {
        var store = new VehicleGeoStore(_connection!, TimeSpan.FromSeconds(120));
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var enriched = new EnrichedPosition(
            new VehiclePosition(
                "hsl", "40/415", "1009_trip", "1009", 60.17, 24.94, 8f, 180f, 45, null, at),
            new ScheduledProgress("1009_trip", "1009", 0, new DateOnly(2026, 11, 23), 100, null),
            45);

        await store.UpsertManyAsync([enriched], Ct);

        var db = _connection!.GetDatabase();
        var position = await db.GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), "40/415");
        Assert.NotNull(position);
        Assert.Equal(24.94, position.Value.Longitude, precision: 5);
        Assert.Equal(60.17, position.Value.Latitude, precision: 5);

        var hash = await db.HashGetAllAsync(VehicleGeoStore.AttributeKey("hsl", "40/415"));
        Assert.Contains(hash, entry => entry.Name == "route_id" && entry.Value == "1009");
        Assert.Contains(hash, entry => entry.Name == "delay_sec" && entry.Value == "45");
        Assert.Contains(hash, entry => entry.Name == "redis_written_at" && !entry.Value.IsNullOrEmpty);
        var ttl = await db.KeyTimeToLiveAsync(VehicleGeoStore.AttributeKey("hsl", "40/415"));
        Assert.NotNull(ttl);
        Assert.True(ttl.Value > TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Upsert_overwrites_the_same_vehicle_member()
    {
        var store = new VehicleGeoStore(_connection!, TimeSpan.FromSeconds(120));
        var t0 = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var first = new EnrichedPosition(
            new VehiclePosition("hsl", "40/415", null, "1009", 60.17, 24.94, null, null, null, null, t0),
            null,
            null);
        var second = new EnrichedPosition(
            new VehiclePosition("hsl", "40/415", null, "1009", 60.18, 24.95, null, null, null, null, t0.AddSeconds(10)),
            null,
            12);

        await store.UpsertManyAsync([first], Ct);
        await store.UpsertManyAsync([second], Ct);

        var db = _connection!.GetDatabase();
        var position = await db.GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), "40/415");
        Assert.NotNull(position);
        Assert.Equal(24.95, position.Value.Longitude, precision: 5);
        Assert.Equal("12", (string?)await db.HashGetAsync(VehicleGeoStore.AttributeKey("hsl", "40/415"), "delay_sec"));
    }
}

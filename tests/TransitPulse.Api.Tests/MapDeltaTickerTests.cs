using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Api.Map;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Tests;

public sealed class MapDeltaTickerTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly GeoBoundingBox _boxA = new(24.9, 60.1, 25.0, 60.2);
    private static readonly GeoBoundingBox _boxB = new(24.8, 60.05, 24.9, 60.15);
    private static readonly ServiceProvider _metricsServices =
        new ServiceCollection().AddMetrics().BuildServiceProvider();

    [Fact]
    public async Task Tick_skips_SendAsync_when_Subscribe_lands_after_CAS()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));
        var generation = Assert.Single(registry.Snapshot()).Generation;

        var hub = new RecordingHubContext();
        var ticker = CreateTicker(registry, hub, [Vehicle("v1", 24.95, 60.15)]);
        ticker.AfterSuccessfulCasForTests = () =>
            registry.Set(new MapSubscription("c1", "hsl", _boxB, RouteId: null));

        await ticker.TickOnceForTestsAsync(
            new MapSubscription("c1", "hsl", _boxA, RouteId: null),
            new Dictionary<string, MapVehicle>(StringComparer.Ordinal),
            generation,
            TestContext.Current.CancellationToken);

        Assert.Empty(hub.Sent);
        Assert.Equal(2L, registry.GetGeneration("c1"));
        Assert.False(registry.IsCurrent("c1", generation));
        Assert.Empty(registry.GetLastSent("c1"));
    }

    [Fact]
    public async Task Tick_skips_SendAsync_when_Subscribe_lands_during_viewport_read()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));
        var generation = Assert.Single(registry.Snapshot()).Generation;

        var hub = new RecordingHubContext();
        var viewport = new FixedViewport([Vehicle("v1", 24.95, 60.15)])
        {
            OnListed = () => registry.Set(new MapSubscription("c1", "hsl", _boxB, RouteId: null)),
        };
        var ticker = CreateTicker(registry, hub, viewport);

        await ticker.TickOnceForTestsAsync(
            new MapSubscription("c1", "hsl", _boxA, RouteId: null),
            new Dictionary<string, MapVehicle>(StringComparer.Ordinal),
            generation,
            TestContext.Current.CancellationToken);

        Assert.Empty(hub.Sent);
        Assert.Equal(2L, registry.GetGeneration("c1"));
        Assert.Empty(registry.GetLastSent("c1"));
    }

    [Fact]
    public async Task Tick_sends_delta_with_generation_when_subscription_is_current()
    {
        var registry = new MapSubscriptionRegistry();
        registry.Set(new MapSubscription("c1", "hsl", _boxA, RouteId: null));
        var generation = Assert.Single(registry.Snapshot()).Generation;

        var hub = new RecordingHubContext();
        var ticker = CreateTicker(registry, hub, [Vehicle("v1", 24.95, 60.15)]);

        await ticker.TickOnceForTestsAsync(
            new MapSubscription("c1", "hsl", _boxA, RouteId: null),
            new Dictionary<string, MapVehicle>(StringComparer.Ordinal),
            generation,
            TestContext.Current.CancellationToken);

        var frame = Assert.Single(hub.Sent);
        Assert.Equal("c1", frame.ConnectionId);
        Assert.Equal(MapDeltaTicker.ClientMethod, frame.Method);
        var delta = Assert.IsType<MapVehicleDelta>(frame.Arg);
        Assert.Equal(generation, delta.Generation);
        Assert.Equal(_at, delta.SentAt);
        Assert.Equal(["v1"], delta.Added.Select(v => v.VehicleId));
        Assert.Contains("v1", registry.GetLastSent("c1").Keys);
    }

    private static MapDeltaTicker CreateTicker(
        MapSubscriptionRegistry registry,
        RecordingHubContext hub,
        IReadOnlyList<MapVehicle> vehicles) =>
        CreateTicker(registry, hub, new FixedViewport(vehicles));

    private static MapDeltaTicker CreateTicker(
        MapSubscriptionRegistry registry,
        RecordingHubContext hub,
        FixedViewport viewport) =>
        new(
            registry,
            viewport,
            hub,
            new MapDeltaMetrics(_metricsServices.GetRequiredService<IMeterFactory>()),
            new FakeTimeProvider(_at),
            NullLogger<MapDeltaTicker>.Instance);

    private static MapVehicle Vehicle(string id, double lon, double lat) =>
        new(id, lon, lat, Bearing: null, RouteId: "1009", TripId: null, DelaySec: null, ObservedAt: _at);

    private sealed class FixedViewport(IReadOnlyList<MapVehicle> vehicles) : IVehicleViewportReader
    {
        public Action? OnListed { get; set; }

        public Task<IReadOnlyList<MapVehicle>> ListInBoxAsync(
            string feedId,
            GeoBoundingBox box,
            string? routeId,
            int limit,
            CancellationToken cancellationToken)
        {
            OnListed?.Invoke();
            return Task.FromResult(vehicles);
        }
    }

    private sealed class RecordingHubContext : IHubContext<VehicleMapHub>
    {
        public List<(string ConnectionId, string Method, object? Arg)> Sent { get; } = [];

        public IHubClients Clients => new RecordingClients(Sent);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClients(List<(string ConnectionId, string Method, object? Arg)> sent) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => new RecordingClient(connectionId, sent);

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RecordingClient(
        string connectionId,
        List<(string ConnectionId, string Method, object? Arg)> sent) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            sent.Add((connectionId, method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }
}

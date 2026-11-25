using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Api.Tests;

public sealed class GtfsCatalogueCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_active_snapshot_is_not_cached_and_returns_null()
    {
        var reader = new FakeReader { ActiveId = null };
        var cache = CreateCache(reader);

        Assert.Null(await cache.GetRoutesAsync("hsl", Ct));
        Assert.Null(await cache.GetRoutesAsync("hsl", Ct));
        Assert.Equal(2, reader.ActiveCalls);
        Assert.Empty(reader.RouteSnapshotCalls);
    }

    [Fact]
    public async Task Factory_reads_the_pinned_snapshot_id_not_the_feed_overload()
    {
        var routes = new[] { new GtfsRoute("1009", "9", "Test", 0) };
        var reader = new FakeReader
        {
            ActiveId = 7,
            RoutesBySnapshot = id => id == 7 ? routes : null,
        };
        var cache = CreateCache(reader);

        var first = await cache.GetRoutesAsync("hsl", Ct);
        var second = await cache.GetRoutesAsync("hsl", Ct);

        Assert.Equal(["1009"], first!.Select(r => r.RouteId));
        Assert.Equal(["1009"], second!.Select(r => r.RouteId));
        Assert.Equal(2, reader.ActiveCalls);
        // One factory call for the pinned id; the second Get hits HybridCache L1.
        Assert.Equal([7], reader.RouteSnapshotCalls);
    }

    [Fact]
    public async Task Dropped_pinned_snapshot_returns_null_and_is_not_served_as_empty()
    {
        var reader = new FakeReader
        {
            ActiveId = 3,
            RoutesBySnapshot = _ => null,
        };
        var cache = CreateCache(reader);

        Assert.Null(await cache.GetRoutesAsync("hsl", Ct));
        Assert.Equal([3], reader.RouteSnapshotCalls);
    }

    [Fact]
    public async Task Unknown_route_shapes_are_cached_as_not_found_not_as_missing_snapshot()
    {
        var reader = new FakeReader
        {
            ActiveId = 4,
            ShapesBySnapshot = (_, _) => new RouteShapesResult(RouteFound: false, Shapes: []),
        };
        var cache = CreateCache(reader);

        var first = await cache.GetRouteShapesAsync("hsl", "missing", Ct);
        var second = await cache.GetRouteShapesAsync("hsl", "missing", Ct);

        Assert.False(first!.RouteFound);
        Assert.False(second!.RouteFound);
        Assert.Equal([(4, "missing")], reader.ShapeSnapshotCalls);
    }

    private static GtfsCatalogueCache CreateCache(IGtfsCatalogueReader reader)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybrid = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new GtfsCatalogueCache(reader, hybrid);
    }

    private sealed class FakeReader : IGtfsCatalogueReader
    {
        public int? ActiveId { get; init; }
        public Func<int, IReadOnlyList<GtfsRoute>?>? RoutesBySnapshot { get; init; }
        public Func<int, string, RouteShapesResult?>? ShapesBySnapshot { get; init; }

        public int ActiveCalls { get; private set; }
        public List<int> RouteSnapshotCalls { get; } = [];
        public List<(int SnapshotId, string RouteId)> ShapeSnapshotCalls { get; } = [];

        public Task<int?> ActiveSnapshotAsync(string feedId, CancellationToken cancellationToken)
        {
            ActiveCalls++;
            return Task.FromResult(ActiveId);
        }

        public Task<IReadOnlyList<GtfsRoute>?> ListRoutesAsync(int snapshotId, CancellationToken cancellationToken)
        {
            RouteSnapshotCalls.Add(snapshotId);
            return Task.FromResult(RoutesBySnapshot?.Invoke(snapshotId));
        }

        public Task<IReadOnlyList<GtfsStop>?> ListStopsAsync(int snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GtfsStop>?>(null);

        public Task<RouteShapesResult?> ListRouteShapesAsync(
            int snapshotId, string routeId, CancellationToken cancellationToken)
        {
            ShapeSnapshotCalls.Add((snapshotId, routeId));
            return Task.FromResult(ShapesBySnapshot?.Invoke(snapshotId, routeId));
        }
    }
}

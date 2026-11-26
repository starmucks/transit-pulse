using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Infrastructure.StopEvents;

namespace TransitPulse.Api.Tests;

public sealed class RealtimeEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stop_events_return_404_for_unknown_stop()
    {
        var catalogue = CreateCatalogue(
            stops: [new GtfsStop("1020453", "Known", 60.1, 24.9, 0, null)]);
        var events = new FakeStopEvents();

        var result = await RealtimeEndpoints.ListStopEventsAsync(
            "hsl", "missing", since: null, limit: null, catalogue, events, Clock(), Ct);

        Assert.IsType<NotFound>(result);
        Assert.Empty(events.Calls);
    }

    [Fact]
    public async Task Stop_events_return_empty_array_for_known_stop_with_no_rows()
    {
        var catalogue = CreateCatalogue(
            stops: [new GtfsStop("1020453", "Known", 60.1, 24.9, 0, null)]);
        var events = new FakeStopEvents { Rows = [] };

        var result = await RealtimeEndpoints.ListStopEventsAsync(
            "hsl", "1020453", since: null, limit: null, catalogue, events, Clock(), Ct);

        var ok = Assert.IsType<Ok<StopEventResponse[]>>(result);
        Assert.Empty(ok.Value!);
        var call = Assert.Single(events.Calls);
        Assert.Equal(_now - TimeSpan.FromMinutes(60), call.Since);
        Assert.Equal(50, call.Limit);
    }

    [Fact]
    public async Task Stop_events_honour_explicit_since_and_limit()
    {
        var catalogue = CreateCatalogue(
            stops: [new GtfsStop("1020453", "Known", 60.1, 24.9, 0, null)]);
        var since = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var row = new ObservedStopEvent(
            "e1", "v1", "t1", "1009", 0, new DateOnly(2026, 11, 5),
            "1020453", 1, "arrival", since.AddMinutes(5), 30);
        var events = new FakeStopEvents { Rows = [row] };

        var result = await RealtimeEndpoints.ListStopEventsAsync(
            "hsl", "1020453", since, limit: 3, catalogue, events, Clock(), Ct);

        var ok = Assert.IsType<Ok<StopEventResponse[]>>(result);
        Assert.Equal(["e1"], ok.Value!.Select(e => e.Id));
        var call = Assert.Single(events.Calls);
        Assert.Equal(since, call.Since);
        Assert.Equal(3, call.Limit);
    }

    [Fact]
    public async Task Punctuality_returns_404_for_unknown_route()
    {
        var catalogue = CreateCatalogue(routes: [new GtfsRoute("1009", "9", "Test", 0)]);
        var punctuality = new FakePunctuality();

        var result = await RealtimeEndpoints.GetRoutePunctualityAsync(
            "hsl", "missing", day: null, catalogue, punctuality, Clock(), Ct);

        Assert.IsType<NotFound>(result);
        Assert.Empty(punctuality.Calls);
    }

    [Fact]
    public async Task Punctuality_returns_zeros_for_known_route_with_empty_day()
    {
        var catalogue = CreateCatalogue(routes: [new GtfsRoute("1009", "9", "Test", 0)]);
        var punctuality = new FakePunctuality
        {
            Summary = new RoutePunctualityDay(
                new DateOnly(2026, 11, 5), "hsl", "1009", 0, 0, null, -60, 180),
        };

        var result = await RealtimeEndpoints.GetRoutePunctualityAsync(
            "hsl", "1009", day: null, catalogue, punctuality, Clock(), Ct);

        var ok = Assert.IsType<Ok<RoutePunctualityResponse>>(result);
        Assert.Equal(0, ok.Value!.Arrivals);
        Assert.Equal(new DateOnly(2026, 11, 5), Assert.Single(punctuality.Calls).Day);
    }

    private static FakeTimeProvider Clock() => new(_now);

    private static GtfsCatalogueCache CreateCatalogue(
        IReadOnlyList<GtfsRoute>? routes = null,
        IReadOnlyList<GtfsStop>? stops = null)
    {
        var reader = new FakeCatalogueReader
        {
            ActiveId = 1,
            Routes = routes ?? [],
            Stops = stops ?? [],
        };
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybrid = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new GtfsCatalogueCache(reader, hybrid);
    }

    private sealed class FakeCatalogueReader : IGtfsCatalogueReader
    {
        public int? ActiveId { get; init; }
        public IReadOnlyList<GtfsRoute> Routes { get; init; } = [];
        public IReadOnlyList<GtfsStop> Stops { get; init; } = [];

        public Task<int?> ActiveSnapshotAsync(string feedId, CancellationToken cancellationToken) =>
            Task.FromResult(ActiveId);

        public Task<IReadOnlyList<GtfsRoute>?> ListRoutesAsync(int snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GtfsRoute>?>(Routes.ToArray());

        public Task<IReadOnlyList<GtfsStop>?> ListStopsAsync(int snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GtfsStop>?>(Stops.ToArray());

        public Task<RouteShapesResult?> ListRouteShapesAsync(
            int snapshotId, string routeId, CancellationToken cancellationToken) =>
            Task.FromResult<RouteShapesResult?>(new RouteShapesResult(false, []));
    }

    private sealed class FakeStopEvents : IStopEventReader
    {
        public IReadOnlyList<ObservedStopEvent> Rows { get; init; } = [];
        public ConcurrentBag<(string FeedId, string StopId, DateTimeOffset Since, int Limit)> Calls { get; } = [];

        public Task<IReadOnlyList<ObservedStopEvent>> ListRecentByStopAsync(
            string feedId, string stopId, DateTimeOffset since, int limit, CancellationToken cancellationToken)
        {
            Calls.Add((feedId, stopId, since, limit));
            return Task.FromResult(Rows);
        }
    }

    private sealed class FakePunctuality : IPunctualityReader
    {
        public RoutePunctualityDay? Summary { get; init; }
        public ConcurrentBag<(string FeedId, string RouteId, DateOnly Day)> Calls { get; } = [];

        public Task<RoutePunctualityDay> GetRouteDayAsync(
            string feedId, string routeId, DateOnly day, CancellationToken cancellationToken)
        {
            Calls.Add((feedId, routeId, day));
            return Task.FromResult(
                Summary ?? new RoutePunctualityDay(day, feedId, routeId, 0, 0, null, -60, 180));
        }
    }
}

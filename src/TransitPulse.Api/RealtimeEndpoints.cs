using TransitPulse.Infrastructure.StopEvents;

namespace TransitPulse.Api;

/// <summary>REST for observed stop events and route punctuality (stage 3.4 demo contract).</summary>
public static class RealtimeEndpoints
{
    private static readonly TimeSpan _defaultLookback = TimeSpan.FromMinutes(60);
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <summary>Maps realtime routes onto <paramref name="app"/>.</summary>
    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").WithTags("Realtime");

        v1.MapGet("/feeds/{feedId}/stops/{stopId}/events", ListStopEventsAsync)
            .WithName("ListStopEvents")
            .WithSummary("Lists recent observed stop events (not schedule ETAs).");

        v1.MapGet("/feeds/{feedId}/routes/{routeId}/punctuality", GetRoutePunctualityAsync)
            .WithName("GetRoutePunctuality")
            .WithSummary("Returns one UTC day of route punctuality from the continuous aggregate.");

        return app;
    }

    /// <summary>Handler for stop events; public so unit tests can call it with fakes.</summary>
    public static async Task<IResult> ListStopEventsAsync(
        string feedId,
        string stopId,
        DateTimeOffset? since,
        int? limit,
        GtfsCatalogueCache catalogue,
        IStopEventReader events,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var stops = await catalogue.GetStopsAsync(feedId, cancellationToken);
        if (stops is null || stops.All(s => s.StopId != stopId))
        {
            return TypedResults.NotFound();
        }

        var windowStart = since ?? time.GetUtcNow() - _defaultLookback;
        var take = ClampLimit(limit);
        var rows = await events.ListRecentByStopAsync(feedId, stopId, windowStart, take, cancellationToken);
        return TypedResults.Ok(rows.Select(ToResponse).ToArray());
    }

    /// <summary>Handler for route punctuality; public so unit tests can call it with fakes.</summary>
    public static async Task<IResult> GetRoutePunctualityAsync(
        string feedId,
        string routeId,
        DateOnly? day,
        GtfsCatalogueCache catalogue,
        IPunctualityReader punctuality,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var routes = await catalogue.GetRoutesAsync(feedId, cancellationToken);
        if (routes is null || routes.All(r => r.RouteId != routeId))
        {
            return TypedResults.NotFound();
        }

        var utcDay = day ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var summary = await punctuality.GetRouteDayAsync(feedId, routeId, utcDay, cancellationToken);
        return TypedResults.Ok(ToResponse(summary));
    }

    private static int ClampLimit(int? limit) =>
        Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static StopEventResponse ToResponse(ObservedStopEvent row) =>
        new(
            row.Id,
            row.VehicleId,
            row.TripId,
            row.RouteId,
            row.DirectionId,
            row.ServiceDate,
            row.StopId,
            row.StopSequence,
            row.Kind,
            row.At,
            row.DelaySec);

    private static RoutePunctualityResponse ToResponse(RoutePunctualityDay summary) =>
        new(
            summary.Day,
            summary.Arrivals,
            summary.OnTimeArrivals,
            summary.AvgArrivalDelaySec,
            summary.OnTimeEarlySec,
            summary.OnTimeLateSec);
}

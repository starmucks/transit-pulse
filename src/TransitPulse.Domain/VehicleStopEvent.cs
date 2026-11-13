using System.Globalization;

namespace TransitPulse.Domain;

/// <summary>
/// A stop event of one vehicle on one scheduled trip, as <c>events.stop</c> carries it: a <see cref="StopEvent"/> with the
/// vehicle and the trip it was found on, without the trip's line and stops.
/// </summary>
/// <param name="FeedId">Source feed, e.g. <c>hsl</c>.</param>
/// <param name="VehicleId">Vehicle identifier, unique within the feed.</param>
/// <param name="TripId">GTFS <c>trip_id</c> of the scheduled trip the vehicle was matched to.</param>
/// <param name="RouteId">GTFS <c>route_id</c> of that trip: the line itself, not a variant a position may report.</param>
/// <param name="DirectionId">GTFS <c>direction_id</c>: 0 or 1.</param>
/// <param name="ServiceDate">The service day the trip belongs to.</param>
/// <param name="StopId">GTFS <c>stop_id</c>.</param>
/// <param name="StopSequence">GTFS <c>stop_sequence</c>: tells apart two calls of one trip at the same stop.</param>
/// <param name="Kind">What the vehicle did.</param>
/// <param name="At">When, as <see cref="StopEvent.At"/> defines it; UTC.</param>
public sealed record VehicleStopEvent(
    string FeedId,
    string VehicleId,
    string TripId,
    string RouteId,
    int DirectionId,
    DateOnly ServiceDate,
    string StopId,
    int StopSequence,
    StopEventKind Kind,
    DateTimeOffset At)
{
    /// <summary>
    /// Identifies the event however often it is found: <c>{FeedId}/{VehicleId}/{TripId}/{ServiceDate}/{StopSequence}/{kind}</c>,
    /// e.g. <c>hsl/18/165/9841_20261030_Ke_1_0800/2026-11-04/12/arrival</c>. Made only of what the event is about, never of
    /// when or where it was processed: positions read again after a restart give the same ids, so a receiver that upserts by
    /// id stores each event once.
    /// </summary>
    /// <remarks>
    /// The vehicle is part of it because two vehicles can report the same trip. The time is not: a repeat may place the
    /// event a little differently, and it is still the same event.
    /// </remarks>
    public string Id => string.Create(
        CultureInfo.InvariantCulture, $"{FeedId}/{VehicleId}/{TripId}/{ServiceDate:yyyy-MM-dd}/{StopSequence}/{KindName(Kind)}");

    /// <summary>The event found for <paramref name="position"/>'s vehicle on <paramref name="trip"/>.</summary>
    /// <param name="position">The position that completed the event: gives the feed and the vehicle.</param>
    /// <param name="trip">The scheduled trip the position was matched to.</param>
    /// <param name="stopEvent">What <see cref="StopDetector"/> found.</param>
    public static VehicleStopEvent From(VehiclePosition position, ScheduledTrip trip, StopEvent stopEvent) => new(
        position.FeedId,
        position.VehicleId,
        trip.TripId,
        trip.RouteId,
        trip.DirectionId,
        trip.ServiceDate,
        stopEvent.Stop.StopId,
        stopEvent.Stop.Sequence,
        stopEvent.Kind,
        stopEvent.At);

    private static string KindName(StopEventKind kind) => kind switch
    {
        StopEventKind.Arrival => "arrival",
        StopEventKind.Departure => "departure",
        StopEventKind.Passed => "passed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown stop event kind."),
    };
}

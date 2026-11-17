namespace TransitPulse.Domain;

/// <summary>A scheduled stop referenced by an enriched position.</summary>
/// <param name="StopId">GTFS <c>stop_id</c>.</param>
/// <param name="StopSequence">GTFS <c>stop_sequence</c>.</param>
/// <param name="ArrivalSeconds">Scheduled arrival after the start of the service day; may exceed 86400.</param>
/// <param name="DepartureSeconds">Scheduled departure after the start of the service day; may exceed 86400.</param>
public sealed record EnrichedStop(
    string StopId, int StopSequence, int? ArrivalSeconds, int? DepartureSeconds);

/// <summary>Where a position lies on the scheduled GTFS trip the source descriptor resolved to.</summary>
/// <param name="TripId">GTFS <c>trip_id</c>.</param>
/// <param name="RouteId">GTFS <c>route_id</c>, not a source-specific route variant.</param>
/// <param name="DirectionId">GTFS <c>direction_id</c>: 0 or 1.</param>
/// <param name="ServiceDate">The local service day the trip belongs to.</param>
/// <param name="AlongRouteMetres">Distance along the trip's shape; <see langword="null"/> when the position was not placed.</param>
/// <param name="NextStop">The stop being approached or occupied, then the following stop after departure.</param>
public sealed record ScheduledProgress(
    string TripId,
    string RouteId,
    int DirectionId,
    DateOnly ServiceDate,
    double? AlongRouteMetres,
    EnrichedStop? NextStop);

/// <summary>
/// One <c>positions.enriched</c> value: the normalized source position, its scheduled progress when resolved, and the best
/// known delay. The source's delay remains in <see cref="VehiclePosition.DelaySec"/> for comparison; <see cref="DelaySec"/>
/// uses it when present and otherwise carries the last delay observed from an actual arrival.
/// </summary>
/// <param name="Position">The normalized source position exactly as <c>positions.raw</c> carries it.</param>
/// <param name="Schedule">Resolved GTFS trip and progress; <see langword="null"/> when no single trip was found.</param>
/// <param name="DelaySec">Best known schedule deviation in seconds, positive when late.</param>
public sealed record EnrichedPosition(VehiclePosition Position, ScheduledProgress? Schedule, int? DelaySec);

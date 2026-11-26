namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>One observed row from <c>public.stop_events</c> for the stop panel (not a schedule ETA).</summary>
/// <param name="Id">Deterministic event id.</param>
/// <param name="VehicleId">Vehicle within the feed.</param>
/// <param name="TripId">Matched GTFS trip.</param>
/// <param name="RouteId">Matched GTFS route.</param>
/// <param name="DirectionId">GTFS direction 0 or 1.</param>
/// <param name="ServiceDate">Service day of the trip.</param>
/// <param name="StopId">GTFS stop id.</param>
/// <param name="StopSequence">GTFS stop sequence on the trip.</param>
/// <param name="Kind"><c>arrival</c>, <c>departure</c>, or <c>passed</c>.</param>
/// <param name="At">When the event was observed (UTC).</param>
/// <param name="DelaySec">Schedule delay when the sink filled it; otherwise <see langword="null"/>.</param>
public sealed record ObservedStopEvent(
    string Id,
    string VehicleId,
    string TripId,
    string RouteId,
    int DirectionId,
    DateOnly ServiceDate,
    string StopId,
    int StopSequence,
    string Kind,
    DateTimeOffset At,
    int? DelaySec);

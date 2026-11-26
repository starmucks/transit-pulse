namespace TransitPulse.Api;

/// <summary>HTTP contract for one observed stop event (not a schedule ETA).</summary>
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
/// <param name="DelaySec">Schedule delay when known.</param>
public sealed record StopEventResponse(
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

/// <summary>HTTP contract for one UTC day of route punctuality.</summary>
/// <param name="Day">UTC calendar day.</param>
/// <param name="Arrivals">Count of arrival events.</param>
/// <param name="OnTimeArrivals">Arrivals inside the on-time window.</param>
/// <param name="AvgArrivalDelaySec">Mean arrival delay when any delay is present.</param>
/// <param name="OnTimeEarlySec">Inclusive early bound of the on-time window (seconds).</param>
/// <param name="OnTimeLateSec">Inclusive late bound of the on-time window (seconds).</param>
public sealed record RoutePunctualityResponse(
    DateOnly Day,
    long Arrivals,
    long OnTimeArrivals,
    double? AvgArrivalDelaySec,
    int OnTimeEarlySec,
    int OnTimeLateSec);

namespace TransitPulse.Domain;

/// <summary>One scheduled run of a GTFS trip: a trip that runs on several days is one of these per day.</summary>
/// <param name="TripId">GTFS <c>trip_id</c>.</param>
/// <param name="RouteId">GTFS <c>route_id</c>, the line itself; a position's own route may be a variant of it.</param>
/// <param name="DirectionId">GTFS <c>direction_id</c>: 0 or 1.</param>
/// <param name="StartSeconds">Departure from the first stop, in seconds since the start of the service day; past 86400 after midnight.</param>
/// <param name="ServiceDate">The service day the run belongs to, which may be the calendar day before its actual time.</param>
public sealed record ScheduledTrip(string TripId, string RouteId, int DirectionId, int StartSeconds, DateOnly ServiceDate);

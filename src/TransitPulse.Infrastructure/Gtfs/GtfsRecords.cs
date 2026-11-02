namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>A row of <c>routes.txt</c>: one line as passengers know it.</summary>
/// <param name="RouteId">Route id, referenced by trips.</param>
/// <param name="ShortName">Line number shown to passengers, e.g. <c>9</c>.</param>
/// <param name="LongName">Full name, usually the terminals.</param>
/// <param name="RouteType">GTFS mode: 0 tram, 1 metro, 2 rail, 3 bus, 4 ferry, extended types above 99.</param>
public sealed record GtfsRoute(string RouteId, string? ShortName, string? LongName, int RouteType);

/// <summary>A row of <c>stops.txt</c>: a stop, platform, station or entrance.</summary>
/// <param name="StopId">Stop id, referenced by stop times and positions.</param>
/// <param name="Name">Name shown to passengers.</param>
/// <param name="Lat">WGS 84 latitude; absent only for generic nodes and boarding areas.</param>
/// <param name="Lon">WGS 84 longitude; absent only for generic nodes and boarding areas.</param>
/// <param name="LocationType">0 stop or platform, 1 station, 2 entrance, 3 generic node, 4 boarding area.</param>
/// <param name="ParentStation">Station the stop belongs to, if any.</param>
public sealed record GtfsStop(string StopId, string? Name, double? Lat, double? Lon, int LocationType, string? ParentStation);

/// <summary>A row of <c>trips.txt</c>: one run of a vehicle along a route on the days of a service.</summary>
/// <param name="TripId">Trip id, referenced by stop times.</param>
/// <param name="RouteId">Route the trip serves.</param>
/// <param name="ServiceId">Service (calendar) that says on which days the trip runs.</param>
/// <param name="DirectionId">0 or 1, if given.</param>
/// <param name="ShapeId">Geometry the vehicle follows, if given.</param>
/// <param name="Headsign">Destination shown on the vehicle.</param>
public sealed record GtfsTrip(string TripId, string RouteId, string ServiceId, int? DirectionId, string? ShapeId, string? Headsign);

/// <summary>A row of <c>stop_times.txt</c>: when a trip calls at one stop.</summary>
/// <remarks>A struct: HSL has about 11 million rows, read once and passed straight on.</remarks>
/// <param name="TripId">Trip that calls.</param>
/// <param name="StopSequence">Order of the call within the trip; increasing, not necessarily consecutive.</param>
/// <param name="StopId">Stop called at.</param>
/// <param name="Arrival">Arrival time since the start of the service day; absent between timepoints.</param>
/// <param name="Departure">Departure time since the start of the service day; absent between timepoints.</param>
/// <param name="ShapeDistTraveled">Distance along the shape, in the feed's own unit, if given.</param>
public readonly record struct GtfsStopTime(
    string TripId, int StopSequence, string StopId, TimeSpan? Arrival, TimeSpan? Departure, double? ShapeDistTraveled);

/// <summary>A row of <c>shapes.txt</c>: one point of a trip geometry.</summary>
/// <param name="ShapeId">Shape the point belongs to.</param>
/// <param name="Sequence">Order of the point within the shape.</param>
/// <param name="Lat">WGS 84 latitude.</param>
/// <param name="Lon">WGS 84 longitude.</param>
/// <param name="DistTraveled">Distance from the first point, in the feed's own unit, if given.</param>
public readonly record struct GtfsShapePoint(string ShapeId, int Sequence, double Lat, double Lon, double? DistTraveled);

/// <summary>A row of <c>calendar.txt</c>: the weekdays a service runs between two dates.</summary>
/// <param name="ServiceId">Service id, referenced by trips.</param>
/// <param name="Days">Weekdays the service runs.</param>
/// <param name="Start">First date, inclusive.</param>
/// <param name="End">Last date, inclusive.</param>
public sealed record GtfsCalendar(string ServiceId, GtfsWeekdays Days, DateOnly Start, DateOnly End);

/// <summary>A row of <c>calendar_dates.txt</c>: a date added to or removed from a service.</summary>
/// <param name="ServiceId">Service id, referenced by trips; may exist only here (Entur has no <c>calendar.txt</c>).</param>
/// <param name="Date">The date.</param>
/// <param name="Added"><see langword="true"/> when the service runs that date, <see langword="false"/> when it does not.</param>
public sealed record GtfsCalendarDate(string ServiceId, DateOnly Date, bool Added);

/// <summary>Weekdays of a <see cref="GtfsCalendar"/>.</summary>
[Flags]
public enum GtfsWeekdays
{
    /// <summary>No weekday.</summary>
    None = 0,

    /// <summary>Monday.</summary>
    Monday = 1,

    /// <summary>Tuesday.</summary>
    Tuesday = 2,

    /// <summary>Wednesday.</summary>
    Wednesday = 4,

    /// <summary>Thursday.</summary>
    Thursday = 8,

    /// <summary>Friday.</summary>
    Friday = 16,

    /// <summary>Saturday.</summary>
    Saturday = 32,

    /// <summary>Sunday.</summary>
    Sunday = 64,
}

namespace TransitPulse.Api;

/// <summary>HTTP contract for one GTFS route.</summary>
/// <param name="RouteId">Agency route id.</param>
/// <param name="ShortName">Short public name, when present.</param>
/// <param name="LongName">Long public name, when present.</param>
/// <param name="RouteType">GTFS <c>route_type</c>.</param>
public sealed record RouteResponse(string RouteId, string? ShortName, string? LongName, int RouteType);

/// <summary>HTTP contract for one stop with coordinates.</summary>
/// <param name="StopId">Agency stop id.</param>
/// <param name="Name">Stop name, when present.</param>
/// <param name="Lat">Latitude.</param>
/// <param name="Lon">Longitude.</param>
/// <param name="LocationType">GTFS <c>location_type</c>.</param>
/// <param name="ParentStation">Parent station id, when present.</param>
public sealed record StopResponse(
    string StopId, string? Name, double Lat, double Lon, int LocationType, string? ParentStation);

/// <summary>One point on a route shape polyline.</summary>
/// <param name="Lat">Latitude.</param>
/// <param name="Lon">Longitude.</param>
public sealed record ShapePointResponse(double Lat, double Lon);

/// <summary>HTTP contract for one route shape.</summary>
/// <param name="ShapeId">GTFS shape id.</param>
/// <param name="Points">Vertices in sequence order.</param>
public sealed record RouteShapeResponse(string ShapeId, IReadOnlyList<ShapePointResponse> Points);

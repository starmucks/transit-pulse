namespace TransitPulse.Infrastructure.Redis;

/// <summary>One vehicle on the hot map layer after a GEO + hash join.</summary>
/// <param name="VehicleId">Member id in the GEO set.</param>
/// <param name="Lon">Longitude from GEO (or hash fallback).</param>
/// <param name="Lat">Latitude from GEO (or hash fallback).</param>
/// <param name="Bearing">Course in degrees, when present.</param>
/// <param name="RouteId">GTFS route id, when present.</param>
/// <param name="TripId">GTFS trip id, when present.</param>
/// <param name="DelaySec">Schedule delay in seconds, when present.</param>
/// <param name="ObservedAt">Last observed time from the attribute hash.</param>
public sealed record MapVehicle(
    string VehicleId,
    double Lon,
    double Lat,
    float? Bearing,
    string? RouteId,
    string? TripId,
    int? DelaySec,
    DateTimeOffset ObservedAt);

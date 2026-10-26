using System.Runtime.CompilerServices;

namespace TransitPulse.Domain;

/// <summary>
/// One observed position of a vehicle: the single model every feed adapter normalizes into.
/// Construction fails on values that can only come from an adapter bug. The plausible ranges of speed, bearing and
/// delay differ per source and are normalized by adapters; here speed and bearing only have to be finite numbers,
/// since NaN or infinity cannot be stored or serialized (an adapter maps such a source value to <c>null</c>).
/// </summary>
/// <param name="FeedId">Source feed, e.g. <c>hsl</c>, <c>entur-sky</c>, <c>mbta</c>.</param>
/// <param name="VehicleId">Vehicle identifier, unique within the feed.</param>
/// <param name="TripId">
/// Trip the vehicle is serving, if known: the GTFS <c>trip_id</c> where the feed sends one, otherwise the GTFS-RT
/// trip descriptor <c>{route_id}/{direction_id}/{start_date}/{start_time}</c> (HSL), so its meaning depends on the feed.
/// </param>
/// <param name="RouteId">Route the vehicle is serving, if known.</param>
/// <param name="Lat">WGS 84 latitude in degrees, [-90, 90].</param>
/// <param name="Lon">WGS 84 longitude in degrees, [-180, 180].</param>
/// <param name="SpeedMps">Speed in metres per second, if reported.</param>
/// <param name="Bearing">Heading in degrees clockwise from north, if reported.</param>
/// <param name="DelaySec">Deviation from schedule in seconds (positive = late), if known.</param>
/// <param name="Occupancy">How full the vehicle is, if reported.</param>
/// <param name="ObservedAt">When the source measured the position; must be in UTC.</param>
public sealed record VehiclePosition(
    string FeedId,
    string VehicleId,
    string? TripId,
    string? RouteId,
    double Lat,
    double Lon,
    float? SpeedMps,
    float? Bearing,
    int? DelaySec,
    OccupancyLevel? Occupancy,
    DateTimeOffset ObservedAt)
{
    /// <summary>Source feed, e.g. <c>hsl</c>, <c>entur-sky</c>, <c>mbta</c>.</summary>
    public string FeedId { get; } = RequireText(FeedId);

    /// <summary>Vehicle identifier, unique within the feed.</summary>
    public string VehicleId { get; } = RequireText(VehicleId);

    /// <summary>WGS 84 latitude in degrees, [-90, 90].</summary>
    public double Lat { get; } = RequireRange(Lat, -90, 90);

    /// <summary>WGS 84 longitude in degrees, [-180, 180].</summary>
    public double Lon { get; } = RequireRange(Lon, -180, 180);

    /// <summary>Speed in metres per second, if reported; never NaN or infinite.</summary>
    public float? SpeedMps { get; } = RequireFinite(SpeedMps);

    /// <summary>Heading in degrees clockwise from north, if reported; never NaN or infinite.</summary>
    public float? Bearing { get; } = RequireFinite(Bearing);

    /// <summary>When the source measured the position; always UTC.</summary>
    public DateTimeOffset ObservedAt { get; } = RequireUtc(ObservedAt);

    private static string RequireText(string value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    // NaN fails too: every comparison with NaN is false.
    private static double RequireRange(double value, double min, double max, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value >= min && value <= max
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"Must be within [{min}, {max}].");

    private static float? RequireFinite(float? value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value is not { } number || float.IsFinite(number)
            ? value
            : throw new ArgumentOutOfRangeException(name, number, "Must be a finite number or null.");

    // Local source time zones are normalized in adapters; a non-zero offset means one was missed.
    // default (0001-01-01) also has offset zero: it means the timestamp was never parsed.
    private static DateTimeOffset RequireUtc(DateTimeOffset value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value == default
            ? throw new ArgumentException("Must be set.", name)
            : value.Offset == TimeSpan.Zero
                ? value
                : throw new ArgumentException($"Must be UTC, got offset {value.Offset}.", name);
}

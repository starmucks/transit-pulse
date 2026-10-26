using System.Globalization;
using System.Text.Json;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// Feed adapter for Helsinki: turns one HFP <c>VP</c> message (one vehicle) into a <see cref="VehiclePosition"/>.
/// <list type="bullet">
/// <item><c>VehicleId</c> is <c>{oper}/{veh}</c>; the delay sign is flipped (HFP <c>dl</c> is positive when ahead).</item>
/// <item><c>TripId</c> is the GTFS-RT trip descriptor <c>{route}/{direction_id}/{yyyyMMdd}/{HH:mm:ss}</c>, with
/// hours past 24 after midnight, as in HSL's own trip updates; HFP carries no GTFS <c>trip_id</c>.</item>
/// <item>Placeholder journeys (<c>jrn</c> 9999, parked trains) keep the position but get no trip, route or delay.</item>
/// <item>Occupancy 0 is <see langword="null"/>: it is every vehicle's default, not a measurement.</item>
/// </list>
/// </summary>
internal sealed class HslPositionNormalizer : IPositionNormalizer
{
    /// <summary>Raw and normalized feed id of HSL positions.</summary>
    public const string FeedId = "hsl";

    /// <summary>Larger offsets are feed errors (measured: up to 12 h); the position is kept without a delay.</summary>
    internal static readonly TimeSpan MaxPlausibleDelay = TimeSpan.FromHours(2);

    private const int PlaceholderJourney = 9999;

    // Helsinki is UTC+2 or +3. The offset only chooses between two departures 24 h apart, so being an hour off in
    // summer cannot change the choice, and the build needs no time zone data (InvariantGlobalization).
    private static readonly TimeSpan _helsinkiStandardOffset = TimeSpan.FromHours(2);

    /// <inheritdoc/>
    public IReadOnlySet<string> RawFeedIds { get; } = new HashSet<string>(StringComparer.Ordinal) { FeedId };

    /// <inheritdoc/>
    public IReadOnlyList<VehiclePosition> Normalize(RawRecord record)
    {
        HfpMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(record.Payload, HfpJsonContext.Default.HfpMessage);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The HFP payload is not valid JSON of a VP event.", ex);
        }

        // No coordinates (location from the odometer only) or no identity: nothing to place on a map.
        if (message?.VP is not { Oper: { } oper, Veh: { } veh, Tst: { } tst, Lat: { } lat, Lon: { } lon } vp
            || lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return [];
        }

        var observedAt = tst.ToUniversalTime();
        var placeholder = vp.Jrn == PlaceholderJourney;
        return
        [
            new VehiclePosition(
                FeedId,
                string.Create(CultureInfo.InvariantCulture, $"{oper}/{veh}"),
                placeholder ? null : TripId(vp, observedAt),
                placeholder ? null : vp.Route,
                lat,
                lon,
                vp.Spd,
                vp.Hdg,
                placeholder ? null : Delay(vp.Dl),
                Occupancy(vp.Occu),
                observedAt),
        ];
    }

    private static int? Delay(int? offset) =>
        offset is { } ahead && Math.Abs(ahead) <= MaxPlausibleDelay.TotalSeconds ? -ahead : null;

    private static OccupancyLevel? Occupancy(int? percent) => percent switch
    {
        null or <= 0 or > 100 => null,
        100 => OccupancyLevel.Full,
        // Only ferries send values in between: a measured passenger share.
        < 50 => OccupancyLevel.ManySeatsAvailable,
        < 90 => OccupancyLevel.FewSeatsAvailable,
        _ => OccupancyLevel.StandingRoomOnly,
    };

    private static string? TripId(HfpVehiclePosition vp, DateTimeOffset observedAt)
    {
        if (vp is not { Route: { } route, Oday: { } odayText, Start: { } startText }
            || vp.Dir is not ("1" or "2")
            || !DateOnly.TryParseExact(odayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var oday)
            || !TimeOnly.TryParseExact(startText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return null;
        }

        // "00:15" on operating day D is either D 00:15 or GTFS 24:15 (D+1 00:15). A vehicle reports only around its
        // journey, so the departure nearer to the observation is the right one; a fixed cut-off hour would not
        // work, since 03:xx is a night journey of D-1 or an early one of D.
        var sameDay = new DateTimeOffset(oday.ToDateTime(start), _helsinkiStandardOffset);
        var hours = start.Hour + (observedAt - sameDay > TimeSpan.FromHours(12) ? 24 : 0);
        var directionId = vp.Dir == "1" ? 0 : 1;
        return string.Create(CultureInfo.InvariantCulture, $"{route}/{directionId}/{oday:yyyyMMdd}/{hours:00}:{start.Minute:00}:00");
    }
}

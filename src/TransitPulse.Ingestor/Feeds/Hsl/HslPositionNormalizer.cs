using System.Globalization;
using System.Text.Json;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// Feed adapter for Helsinki: turns one HFP <c>VP</c> message (one vehicle) into a <see cref="VehiclePosition"/>.
/// <list type="bullet">
/// <item><c>VehicleId</c> is <c>{operator_id}/{vehicle_number}</c> from the MQTT topic, without prefix zeroes; a record
/// without them is dropped as incomplete.</item>
/// <item>The delay sign is flipped (HFP <c>dl</c> is positive when ahead).</item>
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

    // Larger offsets are feed errors (measured: up to 12 h); the position is kept without a delay.
    private const int MaxPlausibleDelaySeconds = 2 * 60 * 60;

    private const int PlaceholderJourney = 9999;

    // Journeys of the previous operating day start at most at 03:xx (measured over four nights); a later start
    // observed 12 h on is a vehicle still reporting an old journey, not a journey of the next calendar day.
    private const int LatestNightStartHour = 5;

    // /hfp/v2/journey/ongoing/vp/{mode}/{operator_id}/{vehicle_number}/...: both come before the free text of the topic.
    private const int TopicOperatorSegment = 7;
    private const int TopicVehicleSegment = 8;

    // Helsinki is UTC+2 or +3. The offset only chooses between two departures 24 h apart, so being an hour off in
    // summer cannot change the choice, and the build needs no time zone data (InvariantGlobalization).
    private static readonly TimeSpan _helsinkiStandardOffset = TimeSpan.FromHours(2);

    /// <inheritdoc/>
    public IReadOnlySet<string> RawFeedIds { get; } = new HashSet<string>(StringComparer.Ordinal) { FeedId };

    /// <inheritdoc/>
    public IReadOnlyList<VehiclePosition> Normalize(RawRecord record, IPositionDrops drops)
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

        // Another event type: not a vehicle record, so nothing was dropped.
        if (message?.VP is not { } vp)
        {
            return [];
        }

        if (VehicleId(record.Topic) is not { } vehicleId || vp.Tst is not { } tst)
        {
            drops.Drop(PositionMetrics.Incomplete);
            return [];
        }

        // ODO coordinates are those of the next stop, not of the vehicle: measured 2026-11-04 08:00 EET, 1.3 km ahead of
        // the bus's next GPS fix. Kept, they would show the vehicle at a stop it has not reached and fake an arrival there.
        // 0.32 % of positions in that hour, mostly buses and trains (docs/benchmarks.md, "HSL location source").
        if (string.Equals(vp.Loc, "ODO", StringComparison.Ordinal))
        {
            drops.Drop(PositionMetrics.NotMeasured);
            return [];
        }

        // No coordinates: nothing to place on a map.
        if (vp is not { Lat: { } lat, Lon: { } lon } || lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            drops.Drop(PositionMetrics.BadLocation);
            return [];
        }

        var observedAt = tst.ToUniversalTime();
        var placeholder = vp.Jrn == PlaceholderJourney;
        return
        [
            new VehiclePosition(
                FeedId,
                vehicleId,
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

    // The topic names the operator that owns the vehicle. The payload's oper is the one running the trip: under
    // subcontracting two owners' vehicles with the same number share it, and their tracks would merge into one.
    private static string? VehicleId(string? topic)
    {
        // One more range than needed: Split leaves the rest of the topic in the last one instead of cutting it up.
        Span<Range> segments = stackalloc Range[TopicVehicleSegment + 2];
        var text = topic.AsSpan();
        var count = text.Split(segments, '/');
        if (count <= TopicVehicleSegment
            || !int.TryParse(text[segments[TopicOperatorSegment]], NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
            || !int.TryParse(text[segments[TopicVehicleSegment]], NumberStyles.None, CultureInfo.InvariantCulture, out var vehicle))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{owner}/{vehicle}");
    }

    // A pattern, not Math.Abs: Math.Abs(int.MinValue) throws and would fail the whole feed for one value.
    private static int? Delay(int? offset) =>
        offset is >= -MaxPlausibleDelaySeconds and <= MaxPlausibleDelaySeconds ? -offset : null;

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

        // "00:15" on operating day D is either D 00:15 or GTFS 24:15 (D+1 00:15). Around its journey a vehicle is
        // nearer to the right departure; a cut-off hour alone would not do, since 03:xx is a night journey of D-1 or
        // an early one of D.
        var sameDay = new DateTimeOffset(oday.ToDateTime(start), _helsinkiStandardOffset);
        var afterMidnight = start.Hour <= LatestNightStartHour && observedAt - sameDay > TimeSpan.FromHours(12);
        var hours = start.Hour + (afterMidnight ? 24 : 0);
        var directionId = vp.Dir == "1" ? 0 : 1;
        return string.Create(CultureInfo.InvariantCulture, $"{route}/{directionId}/{oday:yyyyMMdd}/{hours:00}:{start.Minute:00}:00");
    }
}

using System.Globalization;
using System.Xml;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Entur;

/// <summary>
/// Feed adapter for Bergen: turns one Entur SIRI-VM response (a full snapshot or the changes since the previous poll)
/// into <see cref="VehiclePosition"/>s. Which kind it is does not matter here: repeats are removed by the deduplicator.
/// <list type="bullet">
/// <item><c>TripId</c> is <c>DatedVehicleJourneyRef</c> and <c>RouteId</c> is <c>LineRef</c>, the ids Entur's GTFS uses.</item>
/// <item>A vehicle that has not departed gets no delay when the value is only the time left until departure (often
/// hours): always while <c>assigned</c> to a journey, and when negative while waiting <c>atOrigin</c>. A positive
/// value at the origin is a late departure and is kept.</item>
/// <item>Records measured more than <see cref="MaxRecordAge"/> before the response was received are dropped: a new
/// subscription replays hours-old positions of vehicles that stopped reporting.</item>
/// <item>SIRI sends no speed.</item>
/// </list>
/// </summary>
internal sealed class SiriVmPositionNormalizer : IPositionNormalizer
{
    /// <summary>Normalized feed id of Bergen positions.</summary>
    public const string FeedId = "entur-sky";

    /// <summary>Raw feed id of the SIRI-VM responses.</summary>
    public const string RawFeedId = "entur-sky-vm";

    /// <summary>Oldest record kept, relative to when the response was received (measured: p99 32 s, 0.1% older than this).</summary>
    internal static readonly TimeSpan MaxRecordAge = TimeSpan.FromMinutes(5);

    // Larger offsets are feed errors, as for HSL; the position is kept without a delay.
    private static readonly TimeSpan _maxPlausibleDelay = TimeSpan.FromHours(2);

    /// <inheritdoc/>
    public IReadOnlySet<string> RawFeedIds { get; } = new HashSet<string>(StringComparer.Ordinal) { RawFeedId };

    /// <inheritdoc/>
    public IReadOnlyList<VehiclePosition> Normalize(RawRecord record)
    {
        var positions = new List<VehiclePosition>();
        try
        {
            foreach (var activity in SiriVmReader.Read(new MemoryStream(record.Payload, writable: false)))
            {
                if (Position(activity, record.ReceivedAt) is { } position)
                {
                    positions.Add(position);
                }
            }
        }
        catch (XmlException ex)
        {
            // A cut-off or malformed response: the records read before the error are dropped with it, as the
            // response cannot be trusted.
            throw new FormatException("The SIRI-VM payload is not well-formed XML.", ex);
        }

        return positions;
    }

    // Null skips this vehicle only: one bad record must not cost the other hundreds in the response.
    private static VehiclePosition? Position(SiriVehicleActivity activity, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(activity.VehicleRef)
            || Time(activity.RecordedAtTime) is not { } recordedAt
            || receivedAt - recordedAt > MaxRecordAge
            // "Not within" rather than "outside": every comparison with NaN (which parses from "NaN") is false.
            || Number(activity.Latitude) is not { } lat || lat is not (>= -90 and <= 90)
            || Number(activity.Longitude) is not { } lon || lon is not (>= -180 and <= 180))
        {
            return null;
        }

        return new VehiclePosition(
            FeedId,
            activity.VehicleRef,
            NullIfBlank(activity.DatedVehicleJourneyRef),
            NullIfBlank(activity.LineRef),
            lat,
            lon,
            SpeedMps: null,
            Bearing(activity.Bearing),
            Delay(activity.Delay, activity.VehicleStatus),
            Occupancy(activity.Occupancy),
            recordedAt);
    }

    // xs:dateTime with any number of fractional digits (Entur sends 0-3 in RecordedAtTime, 9 in ResponseTimestamp)
    // and Z or an offset. XmlConvert also accepts a value without a zone (it assumes this machine's) and a time
    // without a date (it takes today's), so only the full form "yyyy-MM-ddTHH:mm:ss…" with a zone gets through.
    private static DateTimeOffset? Time(string? text)
    {
        if (text is not { Length: >= 20 } || text[10] != 'T'
            || !(text[^1] == 'Z' || (text[^6] is '+' or '-' && text[^3] == ':')))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToDateTimeOffset(text).ToUniversalTime();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    // Checked after the cast: 359.99999999 passes as a double but becomes 360f.
    private static float? Bearing(string? text) =>
        Number(text) is { } value && (float)value is >= 0 and < 360 and var bearing ? bearing : null;

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    // SIRI Delay is an xs:duration, positive when late (the model's sign): PT5M43S, -PT1H2M3S.
    private static int? Delay(string? text, string? status)
    {
        if (status == "assigned" || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        TimeSpan delay;
        try
        {
            delay = XmlConvert.ToTimeSpan(text);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return null;
        }

        if (status == "atOrigin" && delay < TimeSpan.Zero)
        {
            return null;
        }

        // Bounds, not delay.Duration(): a duration can parse to TimeSpan.MinValue, whose Duration() throws.
        return delay >= -_maxPlausibleDelay && delay <= _maxPlausibleDelay
            ? (int)Math.Round(delay.TotalSeconds, MidpointRounding.AwayFromZero)
            : null;
    }

    private static OccupancyLevel? Occupancy(string? text) => text switch
    {
        "empty" => OccupancyLevel.Empty,
        // Plain "seats available" is reported by vehicles that also report standingAvailable and full (measured),
        // so it is a measurement; GTFS-RT has no level between many and few seats.
        "manySeatsAvailable" or "seatsAvailable" => OccupancyLevel.ManySeatsAvailable,
        "fewSeatsAvailable" => OccupancyLevel.FewSeatsAvailable,
        "standingAvailable" or "standingRoomOnly" => OccupancyLevel.StandingRoomOnly,
        "crushedStandingRoomOnly" => OccupancyLevel.CrushedStandingRoomOnly,
        "full" => OccupancyLevel.Full,
        "notAcceptingPassengers" => OccupancyLevel.NotAcceptingPassengers,
        _ => null,
    };
}

using System.Diagnostics.Metrics;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Metrics of what normalization makes of each feed's records: positions passed on to publishing, positions dropped
/// and records that yielded none, each with a reason. Tagged <c>feed.id</c> with the raw feed id, the same tag as
/// <see cref="FeedMetrics"/>, so drops can be read as a share of what the feed delivered.
/// </summary>
internal sealed class PositionMetrics
{
    /// <summary>A position observed implausibly long after its record was received.</summary>
    public const string FromTheFuture = "future";

    /// <summary>A position not newer than the vehicle's last one (a repeat or a late, out-of-order report).</summary>
    public const string Duplicate = "duplicate";

    /// <summary>A vehicle record measured too long before its message was received (a vehicle that stopped reporting).</summary>
    public const string Stale = "stale";

    /// <summary>A vehicle record without coordinates or with coordinates off the globe.</summary>
    public const string BadLocation = "bad_location";

    /// <summary>A vehicle record without a vehicle id or a usable measurement time; a jump hints at a changed format.</summary>
    public const string Incomplete = "incomplete";

    /// <summary>A record the normalizer could not parse.</summary>
    public const string Unparsable = "unparsable";

    /// <summary>
    /// A record that parsed but carried no usable position. Includes every record whose vehicles were all dropped
    /// (an HSL message without coordinates counts here and as <see cref="BadLocation"/>), so a steady background is
    /// normal; a changed source format shows first as <see cref="Incomplete"/>.
    /// </summary>
    public const string Empty = "empty";

    /// <summary>A record archived but not published because the position channel was full.</summary>
    public const string Backpressure = "backpressure";

    private const string FeedTag = "feed.id";
    private const string ReasonTag = "reason";

    private readonly Counter<long> _accepted;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _skipped;

    /// <summary>Creates the instruments on the Ingestor's meter.</summary>
    /// <param name="meterFactory">Owns the meter.</param>
    public PositionMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(FeedMetrics.MeterName);
        _accepted = meter.CreateCounter<long>("transitpulse.positions.accepted", "{position}", "Positions passed on to publishing.");
        _dropped = meter.CreateCounter<long>("transitpulse.positions.dropped", "{position}", "Normalized positions dropped, by reason.");
        _skipped = meter.CreateCounter<long>("transitpulse.positions.records_skipped", "{record}", "Raw records that yielded no positions, by reason.");
    }

    /// <summary>Counts positions passed on to publishing.</summary>
    /// <param name="feedId">Raw feed id of their record.</param>
    /// <param name="count">How many; nothing is recorded for zero.</param>
    public void Accepted(string feedId, int count)
    {
        if (count > 0)
        {
            _accepted.Add(count, new KeyValuePair<string, object?>(FeedTag, feedId));
        }
    }

    /// <summary>Counts one dropped position.</summary>
    /// <param name="feedId">Raw feed id of its record.</param>
    /// <param name="reason">
    /// <see cref="FromTheFuture"/> or <see cref="Duplicate"/> from the router; <see cref="Stale"/>,
    /// <see cref="BadLocation"/> or <see cref="Incomplete"/> from a normalizer.
    /// </param>
    public void Dropped(string feedId, string reason) =>
        _dropped.Add(1, new KeyValuePair<string, object?>(FeedTag, feedId), new KeyValuePair<string, object?>(ReasonTag, reason));

    /// <summary>Counts one record that yielded no positions.</summary>
    /// <param name="feedId">Raw feed id of the record.</param>
    /// <param name="reason"><see cref="Unparsable"/>, <see cref="Empty"/> or <see cref="Backpressure"/>.</param>
    public void Skipped(string feedId, string reason) =>
        _skipped.Add(1, new KeyValuePair<string, object?>(FeedTag, feedId), new KeyValuePair<string, object?>(ReasonTag, reason));
}

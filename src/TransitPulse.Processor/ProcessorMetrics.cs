using System.Diagnostics.Metrics;
using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>
/// What the Processor did and how far behind it is. Lag in messages shows how much work has piled up; lag in time shows how
/// stale the output is, which is what the acceptance criterion of stage 2 is about.
/// </summary>
internal sealed class ProcessorMetrics
{
    /// <summary>Meter name; the host exports the meter named after the application.</summary>
    public const string MeterName = "TransitPulse.Processor";

    private static readonly KeyValuePair<string, object?>[] _verdictTags =
    [
        new("verdict", "accepted"),
        new("verdict", "out_of_order"),
        new("verdict", "impossible_jump"),
        new("verdict", "unreadable"),
    ];

    // In the order of TripResolution.
    private static readonly KeyValuePair<string, object?>[] _tripTags =
    [
        new("result", "found"),
        new("result", "no_descriptor"),
        new("result", "malformed"),
        new("result", "day_not_loaded"),
        new("result", "route_unknown"),
        new("result", "no_departure"),
        new("result", "ambiguous"),
        new("result", "no_schedule"),
    ];

    // In the order of RouteMatchStatus.
    private static readonly KeyValuePair<string, object?>[] _routeTags =
    [
        new("status", "on_route"),
        new("status", "off_route"),
        new("status", "no_shape"),
    ];

    private readonly Counter<long> _messages;
    private readonly Counter<long> _trips;
    private readonly Counter<long> _routes;
    private readonly Histogram<double> _lagTime;
    private readonly Histogram<double> _routeOffset;
    private readonly long[] _totals = new long[_verdictTags.Length];
    private readonly long[] _tripTotals = new long[_tripTags.Length];
    private readonly long[] _routeTotals = new long[_routeTags.Length];

    // Replaced as a whole by the loop, read by the exporter's thread.
    private Measurement<long>[] _lag = [];

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">Source of the meter.</param>
    public ProcessorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _messages = meter.CreateCounter<long>("transitpulse.processor.messages", "{message}", "Messages read, by verdict.");
        _trips = meter.CreateCounter<long>("transitpulse.processor.trips", "{position}", "Accepted positions of a feed with a schedule, by what the trip lookup found.");
        _lagTime = meter.CreateHistogram(
            "transitpulse.processor.lag_time",
            "s",
            "Time from a message being handed to the writer's client to it being read: includes the writer's batching.",
            null,
            // The default boundaries start at 5: "under 1 s", the stage's target, would fall into a single bucket.
            new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 300, 1800] });
        _routes = meter.CreateCounter<long>("transitpulse.processor.route", "{position}", "Positions with a found trip, by whether they lie on the trip's line.");
        _routeOffset = meter.CreateHistogram(
            "transitpulse.processor.route_offset",
            "m",
            "Distance from a position to its trip's line, where it was searched.",
            null,
            // Dense around the off-route threshold, which is to be chosen from this distribution.
            new InstrumentAdvice<double> { HistogramBucketBoundaries = [5, 10, 20, 30, 50, 75, 100, 200, 500, 1000] });
        meter.CreateObservableGauge("transitpulse.processor.lag", () => Volatile.Read(ref _lag), "{message}", "Messages in a partition not read yet.");
    }

    /// <summary>Counts one message. Called by the loop only.</summary>
    /// <param name="verdict">What was decided about it.</param>
    /// <param name="lagSeconds">How long ago the writer's client got it; <c>null</c> if the message has no time.</param>
    public void Message(MessageVerdict verdict, double? lagSeconds)
    {
        _totals[(int)verdict]++;
        _messages.Add(1, _verdictTags[(int)verdict]);
        if (lagSeconds is { } lag)
        {
            // Writer and reader clocks can differ slightly; a negative wait means no wait.
            _lagTime.Record(Math.Max(lag, 0));
        }
    }

    /// <summary>Counts one trip lookup. Called by the loop only.</summary>
    /// <param name="result">What it found.</param>
    public void Trip(TripResolution result)
    {
        _tripTotals[(int)result]++;
        _trips.Add(1, _tripTags[(int)result]);
    }

    /// <summary>Counts where a position was placed on its trip's line. Called by the loop only.</summary>
    /// <param name="match">The placement.</param>
    public void Route(RouteMatch match)
    {
        _routeTotals[(int)match.Status]++;
        _routes.Add(1, _routeTags[(int)match.Status]);
        if (match.Status != RouteMatchStatus.NoShape)
        {
            _routeOffset.Record(match.OffsetMetres);
        }
    }

    /// <summary>Placements with this status since the start. Called by the loop only.</summary>
    /// <param name="status">The status.</param>
    public long Total(RouteMatchStatus status) => _routeTotals[(int)status];

    /// <summary>All placements since the start. Called by the loop only.</summary>
    public long RouteMatches => _routeTotals.Sum();

    /// <summary>Messages with this verdict since the start. Called by the loop only.</summary>
    /// <param name="verdict">The verdict.</param>
    public long Total(MessageVerdict verdict) => _totals[(int)verdict];

    /// <summary>Trip lookups with this result since the start. Called by the loop only.</summary>
    /// <param name="result">The result.</param>
    public long Total(TripResolution result) => _tripTotals[(int)result];

    /// <summary>All trip lookups since the start. Called by the loop only.</summary>
    public long TripLookups => _tripTotals.Sum();

    /// <summary>
    /// Publishes the current lag of the partitions this process has read from. Stays as it was while the loop is held up, and
    /// leaves out a partition with nothing read yet, so a backlog right after a restart does not show here at first.
    /// </summary>
    /// <param name="partitions">Partition number and unread messages.</param>
    public void SetLag(IEnumerable<(int Partition, long Lag)> partitions)
    {
        Measurement<long>[] lag = [.. partitions.Select(p => new Measurement<long>(p.Lag, new KeyValuePair<string, object?>("partition", p.Partition)))];
        Volatile.Write(ref _lag, lag);
    }
}

using System.Diagnostics.Metrics;

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

    private readonly Counter<long> _messages;
    private readonly Histogram<double> _lagTime;
    private readonly long[] _totals = new long[_verdictTags.Length];

    // Replaced as a whole by the loop, read by the exporter's thread.
    private Measurement<long>[] _lag = [];

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">Source of the meter.</param>
    public ProcessorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _messages = meter.CreateCounter<long>("transitpulse.processor.messages", "{message}", "Messages read, by verdict.");
        _lagTime = meter.CreateHistogram(
            "transitpulse.processor.lag_time",
            "s",
            "Time from a message being handed to the writer's client to it being read: includes the writer's batching.",
            null,
            // The default boundaries start at 5: "under 1 s", the stage's target, would fall into a single bucket.
            new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 300, 1800] });
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

    /// <summary>Messages with this verdict since the start. Called by the loop only.</summary>
    /// <param name="verdict">The verdict.</param>
    public long Total(MessageVerdict verdict) => _totals[(int)verdict];

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

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Metrics of what each feed delivers, tagged <c>feed.id</c>: records and payload bytes received, and how long ago
/// the feed's last record came. Rates (records per second) are derived from the counters by the metrics backend.
/// </summary>
/// <remarks>
/// Fed by the pipeline's single reader as it takes records from the channel; read by the metrics exporter on its own
/// thread, hence the concurrent dictionary.
/// </remarks>
public sealed class FeedMetrics
{
    /// <summary>Meter name; the service defaults export meters named after the application.</summary>
    public const string MeterName = "TransitPulse.Ingestor";

    private const string FeedTag = "feed.id";

    private readonly TimeProvider _timeProvider;
    private readonly Counter<long> _records;
    private readonly Counter<long> _bytes;
    private readonly ConcurrentDictionary<string, long> _lastRecordAt = new(StringComparer.Ordinal);

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">Owns the meter: the host's in production, a test's own in tests.</param>
    /// <param name="timeProvider">Clock for the time since a feed's last record.</param>
    public FeedMetrics(IMeterFactory meterFactory, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        var meter = meterFactory.Create(MeterName);
        _records = meter.CreateCounter<long>("transitpulse.feed.records", "{record}", "Raw records received from a feed.");
        _bytes = meter.CreateCounter<long>("transitpulse.feed.bytes", "By", "Payload bytes received from a feed.");
        meter.CreateObservableGauge("transitpulse.feed.silence", ObserveSilence, "s", "Time since the feed's last record.");
    }

    /// <summary>Starts the silence clock of every feed, so one that never delivers is reported too.</summary>
    /// <param name="feedIds">All configured feeds.</param>
    public void Started(IEnumerable<string> feedIds)
    {
        var now = _timeProvider.GetTimestamp();
        foreach (var feedId in feedIds)
        {
            _lastRecordAt[feedId] = now;
        }
    }

    /// <summary>Counts one record taken from the channel.</summary>
    /// <param name="record">The record.</param>
    public void Received(RawRecord record)
    {
        var feed = new KeyValuePair<string, object?>(FeedTag, record.FeedId);
        _records.Add(1, feed);
        _bytes.Add(record.Payload.Length, feed);
        _lastRecordAt[record.FeedId] = _timeProvider.GetTimestamp();
    }

    /// <summary>Time since the feed's last record, or since <see cref="Started"/> if none came yet.</summary>
    /// <param name="feedId">Feed to look up.</param>
    /// <returns><see langword="null"/> before <see cref="Started"/> and for unknown feeds.</returns>
    public TimeSpan? SilenceOf(string feedId) =>
        _lastRecordAt.TryGetValue(feedId, out var last) ? _timeProvider.GetElapsedTime(last) : null;

    private IEnumerable<Measurement<double>> ObserveSilence() =>
        _lastRecordAt.Select(feed => new Measurement<double>(
            _timeProvider.GetElapsedTime(feed.Value).TotalSeconds,
            new KeyValuePair<string, object?>(FeedTag, feed.Key)));
}

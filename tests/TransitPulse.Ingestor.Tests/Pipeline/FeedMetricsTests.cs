using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class FeedMetricsTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FeedMetrics _metrics;
    private readonly MetricCollector<long> _bytes;
    private readonly MetricCollector<double> _silence;

    public FeedMetricsTests()
    {
        var meters = TestMeters.Create();
        _metrics = new FeedMetrics(meters, _time);
        _bytes = new MetricCollector<long>(meters, FeedMetrics.MeterName, "transitpulse.feed.bytes");
        _silence = new MetricCollector<double>(meters, FeedMetrics.MeterName, "transitpulse.feed.silence");
    }

    public void Dispose()
    {
        _bytes.Dispose();
        _silence.Dispose();
    }

    [Fact]
    public void Counts_payload_bytes_per_feed()
    {
        _metrics.Received(Record("a", payloadBytes: 100));
        _metrics.Received(Record("a", payloadBytes: 20));
        _metrics.Received(Record("b", payloadBytes: 7));

        var byFeed = _bytes.GetMeasurementSnapshot().GroupBy(Feed).ToDictionary(g => g.Key, g => g.Sum(m => m.Value));
        Assert.Equal(120, byFeed["a"]);
        Assert.Equal(7, byFeed["b"]);
    }

    [Fact]
    public void Silence_grows_from_start_for_a_feed_that_never_delivers()
    {
        _metrics.Started(["quiet"]);
        _time.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(90, Silence()["quiet"]);
    }

    [Fact]
    public void A_record_resets_the_silence_of_its_feed_only()
    {
        _metrics.Started(["a", "b"]);
        _time.Advance(TimeSpan.FromSeconds(30));
        _metrics.Received(Record("a", payloadBytes: 1));
        _time.Advance(TimeSpan.FromSeconds(5));

        var silence = Silence();
        Assert.Equal(5, silence["a"]);
        Assert.Equal(35, silence["b"]);
    }

    private static RawRecord Record(string feedId, int payloadBytes) => new(feedId, DateTimeOffset.UnixEpoch, new byte[payloadBytes], null);

    private static string Feed<T>(CollectedMeasurement<T> measurement)
        where T : struct => (string)measurement.Tags["feed.id"]!;

    private Dictionary<string, double> Silence()
    {
        _silence.RecordObservableInstruments();
        return _silence.GetMeasurementSnapshot().ToDictionary(Feed, m => m.Value);
    }
}

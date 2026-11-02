using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class RunSummaryTests
{
    private readonly IMeterFactory _meters = TestMeters.Create();
    private readonly FakeLogger<RunSummary> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logs_records_positions_and_reasons_per_feed_on_stop()
    {
        using var summary = new RunSummary(_meters, _logger);
        await summary.StartAsync(Ct);
        var feeds = new FeedMetrics(_meters, new FakeTimeProvider());
        var positions = new PositionMetrics(_meters);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            feeds.Received(new RawRecord("hsl", DateTimeOffset.UnixEpoch, [1, 2], null));
        }

        feeds.Received(new RawRecord("weather", DateTimeOffset.UnixEpoch, [1], null));
        positions.Accepted("hsl", 5);
        positions.Dropped("hsl", PositionMetrics.Stale);
        positions.Dropped("hsl", PositionMetrics.Duplicate);
        positions.Dropped("hsl", PositionMetrics.Duplicate);
        positions.Skipped("hsl", PositionMetrics.Empty);
        await summary.StopAsync(Ct);

        Assert.Equal(
            [
                "Run totals hsl: 3 records, 5 positions; dropped duplicate 2, stale 1; skipped empty 1",
                "Run totals weather: 1 records, 0 positions; dropped none; skipped none",
            ],
            _logger.Collector.GetSnapshot().Select(entry => entry.Message));
    }

    [Fact]
    public async Task Counts_only_the_meters_of_its_own_factory()
    {
        using var summary = new RunSummary(_meters, _logger);
        await summary.StartAsync(Ct);

        // Same meter and instrument names, another factory: what parallel tests or a second host would create.
        new PositionMetrics(TestMeters.Create()).Accepted("hsl", 7);
        new PositionMetrics(_meters).Accepted("hsl", 1);
        await summary.StopAsync(Ct);

        Assert.Equal("Run totals hsl: 0 records, 1 positions; dropped none; skipped none", _logger.LatestRecord.Message);
    }

    [Fact]
    public async Task Measurements_after_stop_are_not_counted()
    {
        using var summary = new RunSummary(_meters, _logger);
        await summary.StartAsync(Ct);
        var positions = new PositionMetrics(_meters);
        await summary.StopAsync(Ct);

        positions.Accepted("hsl", 1);

        Assert.Empty(_logger.Collector.GetSnapshot());
    }
}

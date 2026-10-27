using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class FeedSilenceLogTests
{
    private readonly FakeLogger<FeedSilenceLog> _logger = new();
    private readonly FeedSilenceLog _log;

    public FeedSilenceLogTests() => _log = new FeedSilenceLog(_logger);

    /// <summary>A report as <see cref="FeedSilenceHealthCheck"/> produces it, with the given feeds silent.</summary>
    private static HealthReport Report(params (string FeedId, double Silence)[] silent) =>
        Report(new HealthReportEntry(
            silent.Length == 0 ? HealthStatus.Healthy : HealthStatus.Degraded,
            description: null,
            TimeSpan.Zero,
            exception: null,
            silent.ToDictionary(feed => feed.FeedId, feed => (object)feed.Silence)));

    private static HealthReport Report(HealthReportEntry entry) =>
        new(new Dictionary<string, HealthReportEntry> { [FeedSilenceHealthCheck.Name] = entry }, TimeSpan.Zero);

    private void Publish(HealthReport report) => _log.PublishAsync(report, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void Silence_is_logged_once_per_episode()
    {
        Publish(Report(("hsl", 150)));
        Publish(Report(("hsl", 180)));
        Publish(Report(("hsl", 210)));
        Publish(Report());
        Publish(Report());

        var logs = _logger.Collector.GetSnapshot();
        Assert.Equal([LogLevel.Warning, LogLevel.Information], logs.Select(log => log.Level));
        Assert.Equal("Feed hsl is silent: no record for 150 s", logs[0].Message);
        Assert.Equal("Feed hsl delivers again after at least 210 s of silence", logs[1].Message);
    }

    [Fact]
    public void Feeds_have_independent_episodes()
    {
        Publish(Report(("hsl", 150)));
        Publish(Report(("hsl", 180), ("mbta-vp", 200)));
        Publish(Report(("mbta-vp", 230)));
        Publish(Report());

        Assert.Equal(
            [(LogLevel.Warning, "hsl"), (LogLevel.Warning, "mbta-vp"), (LogLevel.Information, "hsl"), (LogLevel.Information, "mbta-vp")],
            _logger.Collector.GetSnapshot().Select(log => (log.Level, (string)log.StructuredState!.Single(p => p.Key == "FeedId").Value!)));
    }

    [Fact]
    public void A_failed_or_missing_check_ends_no_episode()
    {
        Publish(Report(("hsl", 150)));
        Publish(Report(new HealthReportEntry(HealthStatus.Unhealthy, "Check threw", TimeSpan.Zero, new InvalidOperationException(), null)));
        Publish(new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero));
        Publish(Report(("hsl", 210)));

        Assert.Equal(LogLevel.Warning, Assert.Single(_logger.Collector.GetSnapshot()).Level);
    }
}

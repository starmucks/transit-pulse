using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class FeedSilenceHealthCheckTests
{
    private static readonly TimeSpan _limit = TimeSpan.FromMinutes(2);

    private readonly FakeTimeProvider _time = new();
    private readonly FeedMetrics _metrics;

    public FeedSilenceHealthCheckTests() => _metrics = new FeedMetrics(TestMeters.Create(), _time);

    private static ScriptedSource Source(string feedId, TimeSpan? maxSilence) =>
        new(feedId, (_, _, _) => Task.CompletedTask) { MaxSilence = maxSilence };

    private HealthCheckResult Check(params ScriptedSource[] sources)
    {
        _metrics.Started(sources.Select(source => source.FeedId));
        return CheckAgain(sources);
    }

    private HealthCheckResult CheckAgain(params ScriptedSource[] sources) =>
        new FeedSilenceHealthCheck(sources, _metrics).CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult();

    [Fact]
    public void Feeds_that_report_are_healthy()
    {
        Assert.Equal(HealthStatus.Healthy, Check(Source("hsl", _limit)).Status);
    }

    [Fact]
    public void Silence_longer_than_the_limit_degrades_and_names_only_the_silent_feed()
    {
        var hsl = Source("hsl", _limit);
        var mbta = Source("mbta-vp", _limit);
        Check(hsl, mbta);

        _time.Advance(_limit + TimeSpan.FromSeconds(30));
        _metrics.Received(new RawRecord("mbta-vp", _time.GetUtcNow(), [1], null));
        var result = CheckAgain(hsl, mbta);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(150.0, Assert.Single(result.Data, entry => entry.Key == "hsl").Value);
        Assert.Single(result.Data);
        Assert.Equal("Silent: hsl 150 s (limit 120 s)", result.Description);
    }

    [Fact]
    public void Silence_exactly_at_the_limit_is_healthy()
    {
        var hsl = Source("hsl", _limit);
        Check(hsl);

        _time.Advance(_limit);

        Assert.Equal(HealthStatus.Healthy, CheckAgain(hsl).Status);
    }

    [Fact]
    public void Feed_without_a_limit_is_not_checked()
    {
        var alerts = Source("hsl-alerts", maxSilence: null);
        Check(alerts);

        _time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(HealthStatus.Healthy, CheckAgain(alerts).Status);
    }

    [Fact]
    public void A_record_ends_the_silence()
    {
        var hsl = Source("hsl", _limit);
        Check(hsl);
        _time.Advance(_limit * 2);

        _metrics.Received(new RawRecord("hsl", _time.GetUtcNow(), [1], null));

        Assert.Equal(HealthStatus.Healthy, CheckAgain(hsl).Status);
    }
}

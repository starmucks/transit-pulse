using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Normalization;

public sealed class PositionRouterTests
{
    private static PositionRouter Create(params IPositionNormalizer[] normalizers) =>
        new(normalizers, NullLogger<PositionRouter>.Instance);

    [Fact]
    public void Record_goes_to_the_normalizer_of_its_feed()
    {
        var hsl = new FakeNormalizer("hsl", "hsl");
        var mbta = new FakeNormalizer("mbta", "mbta-vp");

        var positions = Create(hsl, mbta).Route(FakeNormalizer.Record("mbta-vp"));

        Assert.Equal("mbta", Assert.Single(positions).FeedId);
        Assert.Equal(0, hsl.Calls);
        Assert.Equal(1, mbta.Calls);
    }

    [Fact]
    public void Feed_without_a_normalizer_yields_nothing()
    {
        Assert.Empty(Create(new FakeNormalizer("hsl", "hsl")).Route(FakeNormalizer.Record("weather")));
    }

    [Fact]
    public void One_normalizer_may_read_several_feeds()
    {
        var mbta = new FakeNormalizer("mbta", "mbta-vp", "mbta-tu") { SilentFeed = "mbta-tu" };
        var router = Create(mbta);

        Assert.Empty(router.Route(FakeNormalizer.Record("mbta-tu")));
        Assert.Single(router.Route(FakeNormalizer.Record("mbta-vp")));
        Assert.Equal(2, mbta.Calls);
    }

    [Fact]
    public void Feed_claimed_twice_fails_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Create(new FakeNormalizer("mbta", "mbta-vp"), new FakeNormalizer("other", "mbta-vp")));

        Assert.Contains("'mbta-vp'", error.Message);
    }

    [Fact]
    public void Failing_record_is_dropped_and_the_next_one_goes_through()
    {
        var hsl = new FakeNormalizer("hsl", "hsl") { FailOnSeconds = 1 };
        var router = Create(hsl);

        Assert.Empty(router.Route(FakeNormalizer.Record("hsl", seconds: 1)));
        Assert.Single(router.Route(FakeNormalizer.Record("hsl", seconds: 2)));
    }

    [Fact]
    public void Failing_feed_is_logged_once_per_episode()
    {
        var logger = new FakeLogger<PositionRouter>();
        var router = new PositionRouter([new FakeNormalizer("hsl", "hsl") { FailOnSeconds = 1 }], logger);

        for (var i = 0; i < 100; i++)
        {
            router.Route(new RawRecord("hsl", FakeNormalizer.T0.AddSeconds(i / 10.0), [1], null));
        }

        // Within the quiet period a success does not end the episode; after it, one summary with the count.
        router.Route(new RawRecord("hsl", FakeNormalizer.T0.AddSeconds(30), [2], null));
        router.Route(new RawRecord("hsl", FakeNormalizer.T0.AddSeconds(80), [3], null));

        var logs = logger.Collector.GetSnapshot();
        Assert.Equal(2, logs.Count);
        Assert.Equal(LogLevel.Warning, logs[0].Level);
        Assert.Contains("100 records", logs[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_position_is_dropped()
    {
        var router = Create(new FakeNormalizer("hsl", "hsl"));

        Assert.Single(router.Route(FakeNormalizer.Record("hsl", seconds: 5)));
        Assert.Empty(router.Route(FakeNormalizer.Record("hsl", seconds: 5)));
        Assert.Single(router.Route(FakeNormalizer.Record("hsl", seconds: 6)));
    }

    [Fact]
    public void Position_from_the_future_is_dropped_and_does_not_silence_the_vehicle()
    {
        var router = Create(new FakeNormalizer("hsl", "hsl"));
        // Observed at T0 + 200 s, but received 10 min before T0: the vehicle's clock is ahead.
        var skewed = new RawRecord("hsl", FakeNormalizer.T0.AddMinutes(-10), [200], null);

        Assert.Empty(router.Route(skewed));
        // Earlier than the dropped time: had it been accepted, this correct position would count as late.
        Assert.Single(router.Route(FakeNormalizer.Record("hsl", seconds: 1)));
    }

    [Fact]
    public void Position_at_the_clock_skew_limit_is_kept()
    {
        var router = Create(new FakeNormalizer("hsl", "hsl"));

        Assert.Single(router.Route(new RawRecord("hsl", FakeNormalizer.T0 - PositionRouter.MaxClockSkew, [0], null)));
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using TransitPulse.Ingestor.Normalization;

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
    public void Repeated_position_is_dropped()
    {
        var router = Create(new FakeNormalizer("hsl", "hsl"));

        Assert.Single(router.Route(FakeNormalizer.Record("hsl", seconds: 5)));
        Assert.Empty(router.Route(FakeNormalizer.Record("hsl", seconds: 5)));
        Assert.Single(router.Route(FakeNormalizer.Record("hsl", seconds: 6)));
    }
}

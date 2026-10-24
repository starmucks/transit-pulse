using Microsoft.Extensions.Logging.Abstractions;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Normalization;

public sealed class PositionRouterTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Raw record whose single payload byte is the number of seconds after <see cref="_t0"/>.</summary>
    private static RawRecord Record(string feedId, byte seconds = 0) => new(feedId, _t0, [seconds], null);

    private static PositionRouter Create(params IPositionNormalizer[] normalizers) =>
        new(normalizers, NullLogger<PositionRouter>.Instance);

    [Fact]
    public void Record_goes_to_the_normalizer_of_its_feed()
    {
        var hsl = new FakeNormalizer("hsl", "hsl");
        var mbta = new FakeNormalizer("mbta", "mbta-vp");

        var positions = Create(hsl, mbta).Route(Record("mbta-vp"));

        Assert.Equal("mbta", Assert.Single(positions).FeedId);
        Assert.Equal(0, hsl.Calls);
        Assert.Equal(1, mbta.Calls);
    }

    [Fact]
    public void Feed_without_a_normalizer_yields_nothing()
    {
        Assert.Empty(Create(new FakeNormalizer("hsl", "hsl")).Route(Record("weather")));
    }

    [Fact]
    public void One_normalizer_may_read_several_feeds()
    {
        var mbta = new FakeNormalizer("mbta", "mbta-vp", "mbta-tu") { SilentFeed = "mbta-tu" };
        var router = Create(mbta);

        Assert.Empty(router.Route(Record("mbta-tu")));
        Assert.Single(router.Route(Record("mbta-vp")));
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

        Assert.Empty(router.Route(Record("hsl", seconds: 1)));
        Assert.Single(router.Route(Record("hsl", seconds: 2)));
    }

    [Fact]
    public void Repeated_position_is_dropped()
    {
        var router = Create(new FakeNormalizer("hsl", "hsl"));

        Assert.Single(router.Route(Record("hsl", seconds: 5)));
        Assert.Empty(router.Route(Record("hsl", seconds: 5)));
        Assert.Single(router.Route(Record("hsl", seconds: 6)));
    }

    /// <summary>
    /// Returns one position of vehicle "v1" observed the payload's byte of seconds after <see cref="_t0"/>;
    /// returns nothing for <see cref="SilentFeed"/> and throws for <see cref="FailOnSeconds"/>.
    /// </summary>
    private sealed class FakeNormalizer(string cityFeedId, params string[] rawFeedIds) : IPositionNormalizer
    {
        public IReadOnlySet<string> RawFeedIds { get; } = rawFeedIds.ToHashSet();

        public string? SilentFeed { get; init; }

        public int? FailOnSeconds { get; init; }

        public int Calls { get; private set; }

        public IReadOnlyList<VehiclePosition> Normalize(RawRecord record)
        {
            Calls++;
            var seconds = record.Payload[0];
            if (seconds == FailOnSeconds)
            {
                throw new FormatException("Broken payload.");
            }

            return record.FeedId == SilentFeed
                ? []
                : [new VehiclePosition(cityFeedId, "v1", null, null, 60.2, 24.9, null, null, null, null, _t0.AddSeconds(seconds))];
        }
    }
}

namespace TransitPulse.Domain.Tests;

public sealed class AnomalyEventMessageTests
{
    [Fact]
    public void Bunching_event_round_trips_with_its_computed_identity()
    {
        var bunching = new BunchingEvent(
            "hsl",
            "1009",
            0,
            "40/415",
            "40/416",
            "follower-trip",
            "leader-trip",
            new DateOnly(2026, 11, 4),
            new DateOnly(2026, 11, 4),
            new DateTimeOffset(2026, 11, 4, 12, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 4, 12, 30, 30, TimeSpan.Zero),
            120,
            600);

        var value = AnomalyEventMessage.Value(bunching);
        var parsed = AnomalyEventMessage.ParseBunching(value);

        Assert.Equal("hsl/1009", AnomalyEventMessage.Key(bunching));
        Assert.Equal(bunching, parsed);
        Assert.Equal(AnomalyKind.Bunching, parsed.Kind);
        Assert.Equal(0.2, parsed.HeadwayRatio);
        Assert.Contains("20261104T123000000Z/bunching", parsed.Id, StringComparison.Ordinal);
    }
}

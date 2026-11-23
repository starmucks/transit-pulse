using TransitPulse.Infrastructure.Positions;

namespace TransitPulse.Infrastructure.Tests.Positions;

public sealed class PositionThinnerTests
{
    private static readonly TimeSpan _tenSeconds = TimeSpan.FromSeconds(10);

    [Fact]
    public void First_point_for_a_vehicle_is_kept()
    {
        var thinner = new PositionThinner(_tenSeconds);
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        Assert.True(thinner.ShouldKeep("hsl", "40/415", at));
    }

    [Fact]
    public void Point_inside_the_window_is_skipped_after_record()
    {
        var thinner = new PositionThinner(_tenSeconds);
        var first = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        thinner.Record("hsl", "40/415", first);

        Assert.False(thinner.ShouldKeep("hsl", "40/415", first.AddSeconds(9)));
        Assert.True(thinner.ShouldKeep("hsl", "40/415", first.AddSeconds(10)));
    }

    [Fact]
    public void Different_vehicles_do_not_share_a_window()
    {
        var thinner = new PositionThinner(_tenSeconds);
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        thinner.Record("hsl", "40/415", at);

        Assert.True(thinner.ShouldKeep("hsl", "40/416", at.AddSeconds(1)));
    }

    [Fact]
    public void Failed_flush_can_retry_the_same_point_until_recorded()
    {
        var thinner = new PositionThinner(_tenSeconds);
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        Assert.True(thinner.ShouldKeep("hsl", "40/415", at));
        Assert.True(thinner.ShouldKeep("hsl", "40/415", at));
        thinner.Record("hsl", "40/415", at);
        Assert.False(thinner.ShouldKeep("hsl", "40/415", at));
    }
}

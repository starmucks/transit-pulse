using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class FailureEpisodesTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FailureEpisodes _episodes = new(TimeSpan.FromMinutes(1));

    private static DateTimeOffset At(int seconds) => _t0.AddSeconds(seconds);

    [Fact]
    public void Only_the_first_failure_starts_an_episode()
    {
        Assert.True(_episodes.Fail("hsl", At(0)));
        Assert.False(_episodes.Fail("hsl", At(1)));
        Assert.False(_episodes.Fail("hsl", At(2)));
    }

    [Fact]
    public void Success_without_an_episode_reports_nothing()
    {
        Assert.Equal(0, _episodes.Succeed("hsl", At(0)));
    }

    [Fact]
    public void Failures_between_successes_stay_one_episode()
    {
        // One bad vehicle among good ones: a failure every second, successes in between.
        Assert.True(_episodes.Fail("hsl", At(0)));
        for (var s = 1; s < 120; s++)
        {
            Assert.Equal(0, _episodes.Succeed("hsl", At(s)));
            Assert.False(_episodes.Fail("hsl", At(s)));
        }
    }

    [Fact]
    public void Success_after_the_quiet_period_ends_the_episode_with_its_count()
    {
        _episodes.Fail("hsl", At(0));
        _episodes.Fail("hsl", At(10));

        Assert.Equal(0, _episodes.Succeed("hsl", At(69)));
        Assert.Equal(2, _episodes.Succeed("hsl", At(70)));
        // Ended: the next failure starts a new episode.
        Assert.True(_episodes.Fail("hsl", At(71)));
    }

    [Fact]
    public void Keys_have_separate_episodes()
    {
        _episodes.Fail("hsl", At(0));

        Assert.True(_episodes.Fail("mbta", At(1)));
        Assert.Equal(1, _episodes.Succeed("hsl", At(60)));
        Assert.Equal(0, _episodes.Succeed("mbta", At(60)));
    }
}

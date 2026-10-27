using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class RestartBackoffTests
{
    private static readonly TimeSpan _shortRun = TimeSpan.FromSeconds(1);

    [Fact]
    public void Delays_double_from_the_initial_delay_up_to_the_cap()
    {
        var backoff = new RestartBackoff(new FixedRandom(0));

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next(_shortRun).TotalSeconds).ToList();

        Assert.Equal([5, 10, 20, 40, 60, 60, 60, 60], delays);
        Assert.Equal(8, backoff.Failures);
    }

    [Fact]
    public void Jitter_only_lengthens_a_delay_and_by_at_most_its_share()
    {
        var shortest = new RestartBackoff(new FixedRandom(0)).Next(_shortRun);
        var longest = new RestartBackoff(new FixedRandom(0.999_999)).Next(_shortRun);

        Assert.Equal(RestartBackoff.Initial, shortest);
        Assert.True(longest < RestartBackoff.Initial * (1 + RestartBackoff.Jitter));
        Assert.True(longest > RestartBackoff.Initial);
    }

    [Fact]
    public void A_healthy_run_starts_again_from_the_initial_delay()
    {
        var backoff = new RestartBackoff(new FixedRandom(0));
        backoff.Next(_shortRun);
        backoff.Next(_shortRun);
        backoff.Next(_shortRun);

        var delay = backoff.Next(RestartBackoff.HealthyRun);

        Assert.Equal(RestartBackoff.Initial, delay);
        Assert.Equal(1, backoff.Failures);
    }

    [Fact]
    public void A_run_just_short_of_healthy_keeps_the_delay_growing()
    {
        var backoff = new RestartBackoff(new FixedRandom(0));
        backoff.Next(_shortRun);

        var delay = backoff.Next(RestartBackoff.HealthyRun - TimeSpan.FromTicks(1));

        Assert.Equal(RestartBackoff.Initial * 2, delay);
    }

    [Fact]
    public void Weeks_of_failures_stay_at_the_cap()
    {
        var backoff = new RestartBackoff(new FixedRandom(0));
        var delay = TimeSpan.Zero;

        for (var i = 0; i < 10_000; i++)
        {
            delay = backoff.Next(_shortRun);
        }

        Assert.Equal(RestartBackoff.Max, delay);
    }

    /// <summary>Returns the same value every time, so delays are exact.</summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}

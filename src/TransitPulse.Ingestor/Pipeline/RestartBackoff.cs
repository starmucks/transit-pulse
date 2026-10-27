namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Delays between restarts of one failing feed source: doubling from <see cref="Initial"/> up to <see cref="Max"/>,
/// back to <see cref="Initial"/> once a run has lasted <see cref="HealthyRun"/>. Each delay is stretched by up to
/// <see cref="Jitter"/> at random, so feeds that failed together (a network outage) do not reconnect in lockstep.
/// </summary>
/// <remarks>
/// Counts runs, not calls of one operation: a source runs for days and fails rarely, so the count must reset after a
/// good run, which a per-call retry policy does not model. Not thread-safe: one instance per source.
/// </remarks>
/// <param name="random">Source of the jitter; shared by all sources in production, fixed in tests.</param>
internal sealed class RestartBackoff(Random random)
{
    /// <summary>Delay after the first failure in a row; jitter only lengthens it, never shortens.</summary>
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(5);

    /// <summary>Longest delay before jitter: a feed back after an outage is picked up within about this long.</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    /// <summary>A run at least this long counts as healthy; longer than a connect attempt or HSL's 10 s check.</summary>
    public static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1);

    /// <summary>Largest random stretch of a delay, as a share of it.</summary>
    public const double Jitter = 0.2;

    // Well past the cap already; bounds the power so a source failing for weeks cannot overflow it.
    private const int MaxDoublings = 16;

    /// <summary>Runs that ended in a row without a healthy one, including the last.</summary>
    public int Failures { get; private set; }

    /// <summary>Counts a run that just ended and returns how long to wait before the next one.</summary>
    /// <param name="lasted">How long the run that ended lasted.</param>
    /// <returns>The delay before the next run: from <see cref="Initial"/> to <see cref="Max"/> plus jitter.</returns>
    public TimeSpan Next(TimeSpan lasted)
    {
        if (lasted >= HealthyRun)
        {
            Failures = 0;
        }

        var doubled = Initial.Ticks * Math.Pow(2, Math.Min(Failures, MaxDoublings));
        Failures++;
        var delay = Math.Min(doubled, Max.Ticks);
        return TimeSpan.FromTicks((long)(delay * (1 + (Jitter * random.NextDouble()))));
    }
}

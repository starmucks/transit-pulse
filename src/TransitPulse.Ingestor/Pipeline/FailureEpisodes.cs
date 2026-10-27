namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Groups repeated failures into episodes so that a failure at stream rate is logged twice (when it starts and when it
/// ends, with a count) instead of once per message. An episode ends only after a quiet period without failures, so a
/// failure that keeps recurring between successes (one bad vehicle among hundreds) stays one episode.
/// </summary>
/// <remarks>
/// Time is the caller's event time (a record's receive time), not the wall clock, so replay behaves like the live
/// stream. Not thread-safe: used from a single reader.
/// </remarks>
/// <param name="quietPeriod">Time without failures after which the next success ends the episode.</param>
internal sealed class FailureEpisodes(TimeSpan quietPeriod)
{
    /// <summary>Quiet period used for the Ingestor's log episodes.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, (long Count, DateTimeOffset LastAt)> _open = new(StringComparer.Ordinal);

    /// <summary>Whether any episode is open; lets callers skip building keys on the common path.</summary>
    public bool AnyOpen => _open.Count > 0;

    /// <summary>Counts a failure.</summary>
    /// <param name="key">What failed, e.g. a feed id or a vehicle.</param>
    /// <param name="at">When it failed.</param>
    /// <returns><see langword="true"/> when the failure starts a new episode: log it in full.</returns>
    public bool Fail(string key, DateTimeOffset at)
    {
        var starts = !_open.TryGetValue(key, out var episode);
        _open[key] = (episode.Count + 1, at);
        return starts;
    }

    /// <summary>Reports a success; ends the episode of <paramref name="key"/> if it has been quiet long enough.</summary>
    /// <param name="key">What succeeded.</param>
    /// <param name="at">When it succeeded.</param>
    /// <returns>The number of failures in the episode that just ended: log it; 0 when no episode ended.</returns>
    public long Succeed(string key, DateTimeOffset at)
    {
        if (!_open.TryGetValue(key, out var episode) || at - episode.LastAt < quietPeriod)
        {
            return 0;
        }

        _open.Remove(key);
        return episode.Count;
    }
}

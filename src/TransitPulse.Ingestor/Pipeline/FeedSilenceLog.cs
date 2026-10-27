using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Turns the periodic <see cref="FeedSilenceHealthCheck"/> results into one log line per silence episode: a warning
/// when a feed turns silent and an information line when it delivers again, rather than a warning every run.
/// </summary>
/// <param name="logger">Log for the episodes.</param>
internal sealed partial class FeedSilenceLog(ILogger<FeedSilenceLog> logger) : IHealthCheckPublisher
{
    // Silent feed id -> its silence in seconds at the latest run, a lower bound of the episode's length.
    private readonly Dictionary<string, double> _silent = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        // A check that threw reports Unhealthy without data: that says nothing about the feeds, so no episode ends.
        if (!report.Entries.TryGetValue(FeedSilenceHealthCheck.Name, out var entry) || entry.Status == HealthStatus.Unhealthy)
        {
            return Task.CompletedTask;
        }

        // Runs come from a timer; the lock keeps a slow run from interleaving with the next one.
        lock (_lock)
        {
            foreach (var (feedId, value) in entry.Data)
            {
                var silence = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (_silent.TryAdd(feedId, silence))
                {
                    LogSilent(feedId, silence);
                }
                else
                {
                    _silent[feedId] = silence;
                }
            }

            foreach (var feedId in _silent.Keys.Where(feedId => !entry.Data.ContainsKey(feedId)).ToList())
            {
                LogDeliversAgain(feedId, _silent[feedId]);
                _silent.Remove(feedId);
            }
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {FeedId} is silent: no record for {Silence} s")]
    private partial void LogSilent(string feedId, double silence);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {FeedId} delivers again after at least {Silence} s of silence")]
    private partial void LogDeliversAgain(string feedId, double silence);
}

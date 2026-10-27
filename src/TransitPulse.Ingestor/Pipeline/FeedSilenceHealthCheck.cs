using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Degraded while any feed with a <see cref="IFeedSource.MaxSilence"/> has delivered no record for longer than that;
/// the result's data maps each silent feed id to its silence in seconds. Degraded rather than unhealthy: the process
/// still archives every other feed, and restarting it would not bring an external source back.
/// </summary>
/// <param name="sources">All configured feeds; those without a limit are not checked.</param>
/// <param name="metrics">Knows when each feed's last record came.</param>
internal sealed class FeedSilenceHealthCheck(IEnumerable<IFeedSource> sources, FeedMetrics metrics) : IHealthCheck
{
    /// <summary>Name the check is registered under.</summary>
    public const string Name = "feed-silence";

    private readonly (string FeedId, TimeSpan Limit)[] _limits =
        [.. sources.Where(source => source.MaxSilence is not null).Select(source => (source.FeedId, source.MaxSilence!.Value))];

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var silent = new Dictionary<string, object>(StringComparer.Ordinal);
        var description = new List<string>();
        foreach (var (feedId, limit) in _limits)
        {
            if (metrics.SilenceOf(feedId) is { } silence && silence > limit)
            {
                // Rounded once: Math.Round and the "0" format round halves differently, so data and text could differ.
                var seconds = Math.Round(silence.TotalSeconds);
                silent[feedId] = seconds;
                description.Add(string.Create(
                    CultureInfo.InvariantCulture, $"{feedId} {seconds} s (limit {limit.TotalSeconds:0} s)"));
            }
        }

        return Task.FromResult(silent.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Silent: {string.Join(", ", description)}", data: silent));
    }
}

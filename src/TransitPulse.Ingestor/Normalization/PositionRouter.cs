using TransitPulse.Domain;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Turns one raw record into the positions to publish: picks the normalizer that reads the record's feed,
/// normalizes it and drops positions observed implausibly far after the record was received, then repeated or late
/// ones. Feeds without a normalizer (weather, alerts, predictions) carry no positions and yield nothing.
/// </summary>
/// <remarks>Not thread-safe: called from a single reader, like the normalizers and the deduplicator it uses.</remarks>
internal sealed partial class PositionRouter
{
    /// <summary>How far after its receive time a position may claim to be observed (vehicle clocks drift).</summary>
    internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, IPositionNormalizer> _byRawFeed = [];
    private readonly PositionDeduplicator _deduplicator = new();
    private readonly FailureEpisodes _normalizeFailures = new(FailureEpisodes.DefaultQuietPeriod);
    private readonly FailureEpisodes _clockSkews = new(FailureEpisodes.DefaultQuietPeriod);
    private readonly PositionMetrics _metrics;
    private readonly ILogger<PositionRouter> _logger;

    /// <summary>Indexes the normalizers by the raw feeds they read.</summary>
    /// <param name="normalizers">One normalizer per city.</param>
    /// <param name="metrics">Counts of accepted and dropped positions.</param>
    /// <param name="logger">Log for messages that fail to normalize.</param>
    /// <exception cref="InvalidOperationException">Two normalizers claim the same raw feed.</exception>
    public PositionRouter(IEnumerable<IPositionNormalizer> normalizers, PositionMetrics metrics, ILogger<PositionRouter> logger)
    {
        _metrics = metrics;
        _logger = logger;
        foreach (var normalizer in normalizers)
        {
            foreach (var rawFeedId in normalizer.RawFeedIds)
            {
                // Checked at startup: otherwise a record would silently go to whichever normalizer was registered first.
                if (!_byRawFeed.TryAdd(rawFeedId, normalizer))
                {
                    throw new InvalidOperationException($"Raw feed '{rawFeedId}' is claimed by more than one normalizer.");
                }
            }
        }
    }

    /// <summary>Normalizes <paramref name="record"/> and returns its new positions.</summary>
    /// <param name="record">A raw record from any feed.</param>
    /// <returns>
    /// Positions to publish; empty for feeds without positions, for repeats, for positions from the future and for
    /// messages that fail to normalize.
    /// </returns>
    public IReadOnlyList<VehiclePosition> Route(RawRecord record)
    {
        if (!_byRawFeed.TryGetValue(record.FeedId, out var normalizer))
        {
            return [];
        }

        IReadOnlyList<VehiclePosition> positions;
        try
        {
            positions = normalizer.Normalize(record);
        }
        catch (Exception ex)
        {
            // Any adapter bug, not only a FormatException: one city's broken message must not stop the others.
            // The record is still in the raw archive, so it can be normalized again after a fix.
            _metrics.Skipped(record.FeedId, PositionMetrics.Unparsable);
            if (_normalizeFailures.Fail(record.FeedId, record.ReceivedAt))
            {
                LogNormalizeFailing(ex, record.FeedId, record.ReceivedAt);
            }

            return [];
        }

        if (positions.Count == 0)
        {
            _metrics.Skipped(record.FeedId, PositionMetrics.Empty);
        }

        if (_normalizeFailures.Succeed(record.FeedId, record.ReceivedAt) is var dropped and > 0)
        {
            LogNormalizeRecovered(record.FeedId, dropped);
        }

        // The record's receive time, not the wall clock: replayed records are judged as they were live.
        var latestPlausible = record.ReceivedAt + MaxClockSkew;
        var accepted = new List<VehiclePosition>(positions.Count);
        foreach (var position in positions)
        {
            // Before the deduplicator: a time from the future would become the vehicle's last time and silence all
            // its correct positions until the process restarts.
            // Keyed per vehicle: one bad clock among hundreds of vehicles must not end and restart a feed-wide episode.
            if (position.ObservedAt > latestPlausible)
            {
                _metrics.Dropped(record.FeedId, PositionMetrics.FromTheFuture);
                if (_clockSkews.Fail(VehicleKey(position), record.ReceivedAt))
                {
                    LogFromTheFuture(position.VehicleId, position.FeedId, position.ObservedAt, record.ReceivedAt);
                }

                continue;
            }

            if (_clockSkews.AnyOpen && _clockSkews.Succeed(VehicleKey(position), record.ReceivedAt) is var skewed and > 0)
            {
                LogClockRecovered(position.VehicleId, position.FeedId, skewed);
            }

            if (_deduplicator.TryAccept(position))
            {
                accepted.Add(position);
            }
            else
            {
                _metrics.Dropped(record.FeedId, PositionMetrics.Duplicate);
            }
        }

        _metrics.Accepted(record.FeedId, accepted.Count);
        return accepted;
    }

    private static string VehicleKey(VehiclePosition position) => $"{position.FeedId}/{position.VehicleId}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Records of feed {FeedId} fail to normalize (first received at {ReceivedAt}); they are dropped until it recovers")]
    private partial void LogNormalizeFailing(Exception exception, string feedId, DateTimeOffset receivedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {FeedId} normalizes again; {Dropped} records were dropped")]
    private partial void LogNormalizeRecovered(string feedId, long dropped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vehicle {VehicleId} of feed {FeedId} reports a time after receipt ({ObservedAt} > {ReceivedAt}); its positions are dropped until its clock recovers")]
    private partial void LogFromTheFuture(string vehicleId, string feedId, DateTimeOffset observedAt, DateTimeOffset receivedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle {VehicleId} of feed {FeedId} reports plausible times again; {Dropped} positions were dropped")]
    private partial void LogClockRecovered(string vehicleId, string feedId, long dropped);
}

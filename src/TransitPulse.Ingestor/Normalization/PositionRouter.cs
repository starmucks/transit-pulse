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
    private readonly ILogger<PositionRouter> _logger;

    /// <summary>Indexes the normalizers by the raw feeds they read.</summary>
    /// <param name="normalizers">One normalizer per city.</param>
    /// <param name="logger">Log for messages that fail to normalize.</param>
    /// <exception cref="InvalidOperationException">Two normalizers claim the same raw feed.</exception>
    public PositionRouter(IEnumerable<IPositionNormalizer> normalizers, ILogger<PositionRouter> logger)
    {
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
            LogNormalizeFailed(ex, record.FeedId, record.ReceivedAt);
            return [];
        }

        // The record's receive time, not the wall clock: replayed records are judged as they were live.
        var latestPlausible = record.ReceivedAt + MaxClockSkew;
        var accepted = new List<VehiclePosition>(positions.Count);
        foreach (var position in positions)
        {
            // Before the deduplicator: a time from the future would become the vehicle's last time and silence all
            // its correct positions until the process restarts.
            if (position.ObservedAt > latestPlausible)
            {
                LogFromTheFuture(record.FeedId, position.VehicleId, position.ObservedAt, record.ReceivedAt);
            }
            else if (_deduplicator.TryAccept(position))
            {
                accepted.Add(position);
            }
        }

        return accepted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Record of feed {FeedId} received at {ReceivedAt} failed to normalize; dropped")]
    private partial void LogNormalizeFailed(Exception exception, string feedId, DateTimeOffset receivedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Position of vehicle {VehicleId} in feed {FeedId} observed at {ObservedAt}, after receipt at {ReceivedAt}; dropped")]
    private partial void LogFromTheFuture(string feedId, string vehicleId, DateTimeOffset observedAt, DateTimeOffset receivedAt);
}

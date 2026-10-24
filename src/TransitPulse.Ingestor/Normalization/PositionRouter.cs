using TransitPulse.Domain;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Turns one raw record into the positions to publish: picks the normalizer that reads the record's feed,
/// normalizes it and drops repeated or late positions. Feeds without a normalizer (weather, alerts, predictions)
/// carry no positions and yield nothing.
/// </summary>
/// <remarks>Not thread-safe: called from a single reader, like the normalizers and the deduplicator it uses.</remarks>
internal sealed partial class PositionRouter
{
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
    /// <returns>Positions to publish; empty for feeds without positions, for repeats and for messages that fail to normalize.</returns>
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

        return positions.Where(_deduplicator.TryAccept).ToList();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Record of feed {FeedId} received at {ReceivedAt} failed to normalize; dropped")]
    private partial void LogNormalizeFailed(Exception exception, string feedId, DateTimeOffset receivedAt);
}

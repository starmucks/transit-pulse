using TransitPulse.Domain;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Passes each vehicle's positions on only while their time moves forward. Drops exact repeats (a polled snapshot
/// showing a vehicle that has not reported since, a full SIRI snapshot after a new requestorId) and late,
/// out-of-order positions, so every vehicle's track in <c>positions.raw</c> is strictly increasing in time.
/// </summary>
/// <remarks>
/// Keeps one timestamp per vehicle (a few thousand for three cities), so memory does not grow with the stream and
/// nothing has to expire. The state lives only in memory: after a restart the first positions may repeat what was
/// already published, which downstream consumers tolerate because they are idempotent (at-least-once delivery).
/// Not thread-safe: the pipeline calls it from its single reader.
/// </remarks>
internal sealed class PositionDeduplicator
{
    private readonly Dictionary<(string FeedId, string VehicleId), DateTimeOffset> _lastObservedAt = [];

    /// <summary>Number of vehicles seen so far.</summary>
    public int VehicleCount => _lastObservedAt.Count;

    /// <summary>
    /// Whether <paramref name="position"/> is newer than the last accepted position of the same vehicle;
    /// if so, it becomes that vehicle's last position.
    /// </summary>
    /// <param name="position">A normalized position.</param>
    /// <returns><see langword="true"/> to publish it; <see langword="false"/> for a repeat or a late position.</returns>
    public bool TryAccept(VehiclePosition position)
    {
        // The feed is part of the key: vehicle ids are only unique within a feed.
        var key = (position.FeedId, position.VehicleId);
        if (_lastObservedAt.TryGetValue(key, out var last) && position.ObservedAt <= last)
        {
            return false;
        }

        _lastObservedAt[key] = position.ObservedAt;
        return true;
    }
}

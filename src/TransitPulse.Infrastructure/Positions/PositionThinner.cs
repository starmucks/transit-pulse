namespace TransitPulse.Infrastructure.Positions;

/// <summary>
/// Keeps at most one warm position per vehicle inside a wall of event time (ADR-0015). Callers check
/// <see cref="ShouldKeep"/> then <see cref="Record"/> when buffering a point so later messages in the same flush window
/// are thinned; a failed flush keeps both the buffer and these records for retry.
/// </summary>
public sealed class PositionThinner
{
    private readonly TimeSpan _minInterval;
    private readonly Dictionary<(string FeedId, string VehicleId), DateTimeOffset> _lastWritten = new();

    /// <summary>Creates a thinner.</summary>
    /// <param name="minInterval">Minimum event-time gap between kept points for the same vehicle; must be positive.</param>
    public PositionThinner(TimeSpan minInterval)
    {
        if (minInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minInterval), minInterval, "Must be positive.");
        }

        _minInterval = minInterval;
    }

    /// <summary>
    /// Whether <paramref name="observedAt"/> is far enough after the last successfully recorded point for this vehicle.
    /// Out-of-order or equal timestamps inside the window are rejected.
    /// </summary>
    public bool ShouldKeep(string feedId, string vehicleId, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);

        var key = (feedId, vehicleId);
        return !_lastWritten.TryGetValue(key, out var last) || observedAt >= last + _minInterval;
    }

    /// <summary>Remembers a point that was written to storage so later points inside the window are skipped.</summary>
    public void Record(string feedId, string vehicleId, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        _lastWritten[(feedId, vehicleId)] = observedAt;
    }
}

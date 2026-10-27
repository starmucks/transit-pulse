namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Receives the vehicle records a normalizer read but rejected, so a feed that turns stale or changes its format shows
/// in the metrics instead of only yielding fewer positions.
/// </summary>
public interface IPositionDrops
{
    /// <summary>Counts one rejected vehicle record.</summary>
    /// <param name="reason">
    /// <see cref="PositionMetrics.Stale"/>, <see cref="PositionMetrics.BadLocation"/> or <see cref="PositionMetrics.Incomplete"/>.
    /// </param>
    void Drop(string reason);
}

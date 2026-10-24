using TransitPulse.Domain;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Destination for normalized positions, such as the <c>positions.raw</c> topic. Called from a single loop,
/// so implementations need no locking and see each vehicle's positions in time order.
/// </summary>
public interface IPositionSink
{
    /// <summary>Accepts the positions of one raw record; may buffer them.</summary>
    /// <param name="positions">Positions to publish, never empty.</param>
    /// <param name="cancellationToken">Cancelled when the shutdown drain budget is used up.</param>
    /// <returns>A task that completes when the positions are accepted (not necessarily delivered).</returns>
    ValueTask PublishAsync(IReadOnlyList<VehiclePosition> positions, CancellationToken cancellationToken);

    /// <summary>Delivers buffered positions; called once, after the last <see cref="PublishAsync"/> on shutdown.</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>A task that completes when everything accepted has been delivered or given up on.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

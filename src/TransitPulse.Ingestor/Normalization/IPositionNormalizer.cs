using TransitPulse.Domain;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Feed adapter that turns one city's raw messages into the unified <see cref="VehiclePosition"/> model.
/// One normalizer may read several raw feeds of its city (for example positions plus a feed it only takes context
/// from, returning nothing for its messages); today each reads one.
/// The pipeline calls it from a single reader, so implementations may keep state without locking.
/// </summary>
public interface IPositionNormalizer
{
    /// <summary>Raw feed ids this normalizer reads, e.g. <c>mbta-vp</c>; each id belongs to one normalizer.</summary>
    IReadOnlySet<string> RawFeedIds { get; }

    /// <summary>Extracts the positions contained in one raw message.</summary>
    /// <param name="record">A message from one of <see cref="RawFeedIds"/>.</param>
    /// <returns>The positions in the message, or an empty list when it carries none.</returns>
    /// <exception cref="FormatException">The payload cannot be parsed; the caller drops the message and keeps going.</exception>
    IReadOnlyList<VehiclePosition> Normalize(RawRecord record);
}

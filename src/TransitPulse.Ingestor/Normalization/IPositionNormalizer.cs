using TransitPulse.Domain;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Feed adapter that turns one city's raw messages into the unified <see cref="VehiclePosition"/> model.
/// One normalizer may read several raw feeds of its city: MBTA positions carry no delay, so its normalizer also
/// reads trip updates, remembers their delays and returns nothing for them.
/// The pipeline calls it from a single reader, so implementations may keep state without locking.
/// </summary>
public interface IPositionNormalizer
{
    /// <summary>Raw feed ids this normalizer reads, e.g. <c>mbta-vp</c> and <c>mbta-tu</c>; each id belongs to one normalizer.</summary>
    IReadOnlySet<string> RawFeedIds { get; }

    /// <summary>Extracts the positions contained in one raw message.</summary>
    /// <param name="record">A message from one of <see cref="RawFeedIds"/>.</param>
    /// <returns>The positions in the message, or an empty list when it carries none (e.g. a trip update).</returns>
    /// <exception cref="FormatException">The payload cannot be parsed; the caller drops the message and keeps going.</exception>
    IReadOnlyList<VehiclePosition> Normalize(RawRecord record);
}

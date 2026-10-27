using Google.Protobuf;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using Rt = TransitRealtime;

namespace TransitPulse.Ingestor.Feeds.GtfsRt;

/// <summary>
/// Feed adapter for any GTFS Realtime <c>VehiclePositions</c> feed (Boston's MBTA): turns one polled snapshot into
/// <see cref="VehiclePosition"/>s. Nothing in it is specific to a city, so another GTFS-RT city is one more instance
/// with its own feed ids. Vehicles that have not reported since the previous snapshot keep their timestamp and are
/// removed by the deduplicator, so no snapshot diff is needed here.
/// <list type="bullet">
/// <item><c>VehicleId</c> is <c>vehicle.id</c>; <c>TripId</c> and <c>RouteId</c> are the GTFS ids of the trip descriptor.</item>
/// <item>No delay: GTFS-RT positions carry none, and MBTA trip updates send only absolute predicted times (measured:
/// no <c>delay</c> in 1.83 M trip updates of 2026-10-26). The delay against the schedule is computed downstream.</item>
/// <item>Records measured more than <see cref="MaxRecordAge"/> before the snapshot was received are dropped:
/// vehicles that stopped reporting stay in the snapshot with their last position (measured: 1.7% of records).</item>
/// <item>Fields the feed did not set stay <c>null</c>: an unset speed is not a vehicle standing still.</item>
/// </list>
/// </summary>
/// <param name="feedId">Normalized feed id of the positions, e.g. <c>mbta</c>.</param>
/// <param name="rawFeedId">Raw feed id of the polled snapshots, e.g. <c>mbta-vp</c>.</param>
internal sealed class GtfsRtPositionNormalizer(string feedId, string rawFeedId) : IPositionNormalizer
{
    /// <summary>Oldest record kept, relative to when the snapshot was received; the same limit as for Entur.</summary>
    internal static readonly TimeSpan MaxRecordAge = TimeSpan.FromMinutes(5);

    // The largest POSIX time DateTimeOffset can hold (9999-12-31T23:59:59Z); a larger one is a feed error.
    private static readonly ulong _maxUnixSeconds = (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <inheritdoc/>
    public IReadOnlySet<string> RawFeedIds { get; } = new HashSet<string>(StringComparer.Ordinal) { rawFeedId };

    /// <inheritdoc/>
    public IReadOnlyList<VehiclePosition> Normalize(RawRecord record)
    {
        Rt.FeedMessage message;
        try
        {
            message = Rt.FeedMessage.Parser.ParseFrom(record.Payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new FormatException("The GTFS Realtime payload is not a valid FeedMessage.", ex);
        }

        var positions = new List<VehiclePosition>(message.Entity.Count);
        foreach (var entity in message.Entity)
        {
            if (!entity.IsDeleted && entity.Vehicle is { } vehicle && Position(vehicle, record.ReceivedAt) is { } position)
            {
                positions.Add(position);
            }
        }

        return positions;
    }

    // Null skips this vehicle only: one bad record must not cost the other hundreds in the snapshot.
    private VehiclePosition? Position(Rt.VehiclePosition vehicle, DateTimeOffset receivedAt)
    {
        if (vehicle.Vehicle?.Id is not { } vehicleId || string.IsNullOrWhiteSpace(vehicleId)
            || vehicle.Position is not { } position
            || !vehicle.HasTimestamp || vehicle.Timestamp is 0 || vehicle.Timestamp > _maxUnixSeconds)
        {
            return null;
        }

        var observedAt = DateTimeOffset.FromUnixTimeSeconds((long)vehicle.Timestamp);

        // "Not within" rather than "outside": every comparison with NaN is false.
        if (receivedAt - observedAt > MaxRecordAge
            || position.Latitude is not (>= -90 and <= 90)
            || position.Longitude is not (>= -180 and <= 180))
        {
            return null;
        }

        return new VehiclePosition(
            feedId,
            vehicleId,
            NullIfBlank(vehicle.Trip?.TripId),
            NullIfBlank(vehicle.Trip?.RouteId),
            position.Latitude,
            position.Longitude,
            position.HasSpeed && position.Speed is >= 0 and var speed && float.IsFinite(speed) ? speed : null,
            position.HasBearing && position.Bearing is >= 0 and < 360 and var bearing ? bearing : null,
            DelaySec: null,
            vehicle.HasOccupancyStatus ? Occupancy(vehicle.OccupancyStatus) : null,
            observedAt);
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    // Spelled out rather than cast: the numbers match today, but NO_DATA_AVAILABLE and NOT_BOARDABLE (7, 8) mean
    // "unknown", and a later spec value must not become an undefined OccupancyLevel.
    private static OccupancyLevel? Occupancy(Rt.VehiclePosition.Types.OccupancyStatus status) => status switch
    {
        Rt.VehiclePosition.Types.OccupancyStatus.Empty => OccupancyLevel.Empty,
        Rt.VehiclePosition.Types.OccupancyStatus.ManySeatsAvailable => OccupancyLevel.ManySeatsAvailable,
        Rt.VehiclePosition.Types.OccupancyStatus.FewSeatsAvailable => OccupancyLevel.FewSeatsAvailable,
        Rt.VehiclePosition.Types.OccupancyStatus.StandingRoomOnly => OccupancyLevel.StandingRoomOnly,
        Rt.VehiclePosition.Types.OccupancyStatus.CrushedStandingRoomOnly => OccupancyLevel.CrushedStandingRoomOnly,
        Rt.VehiclePosition.Types.OccupancyStatus.Full => OccupancyLevel.Full,
        Rt.VehiclePosition.Types.OccupancyStatus.NotAcceptingPassengers => OccupancyLevel.NotAcceptingPassengers,
        _ => null,
    };
}

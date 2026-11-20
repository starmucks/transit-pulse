using System.Text.Json;
using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>
/// What the Processor keeps for one partition: the tracks of its vehicles, their progress along their trips and stops, and
/// how far it has read. Kept per partition
/// so a partition's state can leave with it in a rebalance. Not thread-safe: used only by the consume loop.
/// </summary>
/// <param name="schedule">Where accepted positions look up their trips.</param>
internal sealed class PartitionState(CurrentTripSchedule schedule)
{
    private readonly Dictionary<string, VehicleState> _vehicles = [];

    // Reused for every message: events are rare, a new list per position would not be.
    private readonly List<StopEvent> _found = [];
    private readonly List<VehicleStopEvent> _stopEvents = [];
    private long? _committedOffset;

    /// <summary>The offset to resume from: one past the last message processed, as Kafka expects a commit to be.</summary>
    public long? NextOffset { get; private set; }

    /// <summary>Whether messages were processed since the last commit; an idle partition has nothing to commit.</summary>
    public bool HasUncommitted => NextOffset != _committedOffset;

    /// <summary>
    /// The stop events the last <see cref="Process"/> call found, in the order they happened; empty for most positions.
    /// Valid until the next call.
    /// </summary>
    public IReadOnlyList<VehicleStopEvent> StopEvents => _stopEvents;

    internal PartitionStateSnapshot Capture()
    {
        var vehicles = new PartitionVehicleSnapshot[_vehicles.Count];
        var index = 0;
        foreach (var (key, vehicle) in _vehicles)
        {
            vehicles[index++] = new PartitionVehicleSnapshot(key, vehicle.Capture());
        }

        Array.Sort(vehicles, static (first, second) => string.CompareOrdinal(first.Key, second.Key));
        return new PartitionStateSnapshot(NextOffset, vehicles);
    }

    internal static PartitionState Restore(PartitionStateSnapshot snapshot, CurrentTripSchedule schedule)
        => Restore(snapshot, schedule, schedule.ReadCurrent());

    internal static PartitionState Restore(
        PartitionStateSnapshot snapshot,
        CurrentTripSchedule schedule,
        TripSchedule? currentSchedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (snapshot.Vehicles is null
            || snapshot.NextOffset is < 0
            || snapshot.NextOffset is null && snapshot.Vehicles.Length > 0)
        {
            throw new ArgumentException("The partition snapshot is inconsistent.", nameof(snapshot));
        }

        var state = new PartitionState(schedule)
        {
            NextOffset = snapshot.NextOffset,
        };
        foreach (var vehicle in snapshot.Vehicles)
        {
            if (string.IsNullOrWhiteSpace(vehicle.Key)
                || !state._vehicles.TryAdd(vehicle.Key, VehicleState.Restore(vehicle.State, currentSchedule)))
            {
                throw new ArgumentException("The partition snapshot contains an invalid or duplicate vehicle key.", nameof(snapshot));
            }
        }

        return state;
    }

    /// <summary>Judges one message and moves the partition past it, whatever the verdict.</summary>
    /// <param name="offset">The message's offset.</param>
    /// <param name="key">The message's key: identifies the vehicle within the system.</param>
    /// <param name="value">The message's value, written by <see cref="PositionMessage.Value"/>.</param>
    /// <returns>The verdict and, for an accepted position, what the trip lookup found and where on the trip's line it lies.</returns>
    public ProcessedMessage Process(long offset, string? key, ReadOnlySpan<byte> value)
    {
        if (key is null || !TryParse(value, out var position))
        {
            return RejectUnreadable(offset);
        }

        return ProcessParsed(offset, key, position);
    }

    internal ProcessedMessage ProcessParsed(long offset, string key, VehiclePosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(position);
        NextOffset = offset + 1;
        _stopEvents.Clear();

        if (!_vehicles.TryGetValue(key, out var vehicle))
        {
            vehicle = new VehicleState();
            _vehicles.Add(key, vehicle);
        }

        return vehicle.Track.Check(position) switch
        {
            PositionCheck.Accepted => Accept(vehicle, position),
            PositionCheck.OutOfOrder => new ProcessedMessage(MessageVerdict.OutOfOrder, null),
            PositionCheck.ImpossibleJump => new ProcessedMessage(MessageVerdict.ImpossibleJump, null),
            var check => throw new InvalidOperationException($"Unknown position check {check}."),
        };
    }

    internal ProcessedMessage RejectUnreadable(long offset)
    {
        NextOffset = offset + 1;
        _stopEvents.Clear();
        return new ProcessedMessage(MessageVerdict.Unreadable, null);
    }

    /// <summary>Records a successful commit.</summary>
    /// <param name="offset">The committed offset: the value <see cref="NextOffset"/> had when the commit was made.</param>
    public void MarkCommitted(long offset) => _committedOffset = offset;

    // A position without a found trip leaves the progress and the stop detection as they were: the vehicle may be back on
    // the same trip next time.
    private ProcessedMessage Accept(VehicleState vehicle, VehiclePosition position)
    {
        var resolution = schedule.Resolve(position, out var trip);
        if (trip is null)
        {
            return new ProcessedMessage(
                MessageVerdict.Accepted,
                resolution,
                Position: new EnrichedPosition(position, null, position.DelaySec));
        }

        vehicle.BeginTrip(trip);
        var route = vehicle.Route.Advance(trip, position.Lat, position.Lon, position.ObservedAt);
        vehicle.Stops.Advance(trip, route, position.ObservedAt, _found);
        DelayCheck? delay = null;
        foreach (var found in _found)
        {
            _stopEvents.Add(VehicleStopEvent.From(position, trip, found));
            if (found.Kind == StopEventKind.Arrival)
            {
                delay = ObserveDelay(vehicle, trip, found);
            }
        }

        _found.Clear();
        if (trip.Stops is not null && route.Status == RouteMatchStatus.OnRoute)
        {
            vehicle.RememberDetectorPosition(position);
        }

        var progress = new ScheduledProgress(
            trip.TripId,
            trip.RouteId,
            trip.DirectionId,
            trip.ServiceDate,
            route.Status == RouteMatchStatus.OnRoute ? route.AlongMetres : null,
            route.Status == RouteMatchStatus.OnRoute ? NextStop(vehicle.Stops.NextStop, trip.Timetable) : null);
        var enriched = new EnrichedPosition(position, progress, position.DelaySec ?? vehicle.ObservedDelaySec);
        return new ProcessedMessage(MessageVerdict.Accepted, resolution, route, enriched, delay, trip);
    }

    private DelayCheck ObserveDelay(VehicleState vehicle, ScheduledTrip trip, StopEvent arrival)
    {
        if (trip.Timetable is not { } timetable
            || !timetable.TryFind(arrival.Stop.Sequence, out var call)
            || call.ArrivalSeconds is not { } scheduled)
        {
            return new DelayCheck(DelayCheckResult.NoScheduledArrival);
        }

        var calculated = ScheduleDeviation.InSeconds(trip.ServiceDate, scheduled, arrival.At, schedule.TimeZone);
        vehicle.ObservedDelaySec = calculated;
        return vehicle.DetectorPositionAt == arrival.At && vehicle.DetectorDelaySec is { } source
            ? new DelayCheck(DelayCheckResult.Compared, calculated, source)
            : new DelayCheck(DelayCheckResult.NoSourceDelay, calculated);
    }

    private static EnrichedStop? NextStop(StopCall? stop, StopTimetable? timetable)
    {
        if (stop is not { } next)
        {
            return null;
        }

        var times = default(ScheduledStopTime);
        timetable?.TryFind(next.Sequence, out times);
        return new EnrichedStop(next.StopId, next.Sequence, times.ArrivalSeconds, times.DepartureSeconds);
    }

    private static bool TryParse(ReadOnlySpan<byte> value, out VehiclePosition position)
    {
        try
        {
            position = PositionMessage.Parse(value);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            position = null!;
            return false;
        }
    }

    // Everything kept about one vehicle, in one place: one lookup per message.
    private sealed class VehicleState
    {
        private string? _delayTripId;
        private DateOnly _delayServiceDate;
        private StopTimetable? _delayTimetable;

        public VehicleTrack Track { get; private init; } = new();

        public RouteProgress Route { get; private init; } = new();

        public StopDetector Stops { get; private init; } = new();

        public int? ObservedDelaySec { get; set; }

        public DateTimeOffset? DetectorPositionAt { get; private set; }

        public int? DetectorDelaySec { get; private set; }

        public VehicleStateSnapshot Capture() => new(
            Track.Capture(),
            Route.Capture(),
            Stops.Capture(),
            _delayTripId,
            _delayServiceDate,
            ObservedDelaySec,
            DetectorPositionAt,
            DetectorDelaySec);

        public static VehicleState Restore(VehicleStateSnapshot snapshot, TripSchedule? schedule)
        {
            StopPattern? pattern = null;
            if (snapshot.Stops.HasPattern)
            {
                var stopTrip = Rebind(schedule, snapshot.Stops.TripId!, snapshot.Stops.ServiceDate);
                pattern = stopTrip.Stops
                    ?? throw new InvalidOperationException($"Trip {stopTrip.TripId} on {stopTrip.ServiceDate} has no stop pattern.");
            }

            StopTimetable? timetable = null;
            if (snapshot.DelayTripId is null)
            {
                if (snapshot.DelayServiceDate != default
                    || snapshot.ObservedDelaySec is not null
                    || snapshot.DetectorPositionAt is not null
                    || snapshot.DetectorDelaySec is not null)
                {
                    throw new ArgumentException("The vehicle snapshot has delay state without a trip.", nameof(snapshot));
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(snapshot.DelayTripId)
                    || snapshot.DelayServiceDate == default
                    || snapshot.DetectorPositionAt is { } detectorAt
                        && (detectorAt == default || detectorAt.Offset != TimeSpan.Zero)
                    || snapshot.DetectorDelaySec is not null && snapshot.DetectorPositionAt is null)
                {
                    throw new ArgumentException("The vehicle delay snapshot is inconsistent.", nameof(snapshot));
                }

                timetable = Rebind(schedule, snapshot.DelayTripId, snapshot.DelayServiceDate).Timetable;
            }

            return new VehicleState
            {
                Track = VehicleTrack.Restore(snapshot.Track),
                Route = RouteProgress.Restore(snapshot.Route),
                Stops = StopDetector.Restore(snapshot.Stops, pattern),
                _delayTripId = snapshot.DelayTripId,
                _delayServiceDate = snapshot.DelayServiceDate,
                _delayTimetable = timetable,
                ObservedDelaySec = snapshot.ObservedDelaySec,
                DetectorPositionAt = snapshot.DetectorPositionAt,
                DetectorDelaySec = snapshot.DetectorDelaySec,
            };
        }

        public void BeginTrip(ScheduledTrip trip)
        {
            if (!string.Equals(_delayTripId, trip.TripId, StringComparison.Ordinal)
                || _delayServiceDate != trip.ServiceDate
                || !ReferenceEquals(_delayTimetable, trip.Timetable))
            {
                _delayTripId = trip.TripId;
                _delayServiceDate = trip.ServiceDate;
                _delayTimetable = trip.Timetable;
                ObservedDelaySec = null;
                DetectorPositionAt = null;
                DetectorDelaySec = null;
            }
        }

        public void RememberDetectorPosition(VehiclePosition position)
        {
            DetectorPositionAt = position.ObservedAt;
            DetectorDelaySec = position.DelaySec;
        }

        private static ScheduledTrip Rebind(TripSchedule? schedule, string tripId, DateOnly serviceDate) =>
            schedule is not null && schedule.TryFindByIdentity(tripId, serviceDate, out var trip)
                ? trip!
                : throw new InvalidOperationException($"Trip {tripId} on {serviceDate} is absent from the current schedule.");
    }
}

internal readonly record struct PartitionStateSnapshot(long? NextOffset, PartitionVehicleSnapshot[] Vehicles);

internal readonly record struct PartitionVehicleSnapshot(string Key, VehicleStateSnapshot State);

internal readonly record struct VehicleStateSnapshot(
    VehicleTrackSnapshot Track,
    RouteProgressSnapshot Route,
    StopDetectorSnapshot Stops,
    string? DelayTripId,
    DateOnly DelayServiceDate,
    int? ObservedDelaySec,
    DateTimeOffset? DetectorPositionAt,
    int? DetectorDelaySec);

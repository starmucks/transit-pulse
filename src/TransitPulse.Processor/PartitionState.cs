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

    /// <summary>Judges one message and moves the partition past it, whatever the verdict.</summary>
    /// <param name="offset">The message's offset.</param>
    /// <param name="key">The message's key: identifies the vehicle within the system.</param>
    /// <param name="value">The message's value, written by <see cref="PositionMessage.Value"/>.</param>
    /// <returns>The verdict and, for an accepted position, what the trip lookup found and where on the trip's line it lies.</returns>
    public ProcessedMessage Process(long offset, string? key, ReadOnlySpan<byte> value)
    {
        // An unreadable message moves the offset too: it would fail the same way on every retry and block the partition.
        NextOffset = offset + 1;
        _stopEvents.Clear();

        if (key is null || !TryParse(value, out var position))
        {
            return new ProcessedMessage(MessageVerdict.Unreadable, null);
        }

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

        public VehicleTrack Track { get; } = new();

        public RouteProgress Route { get; } = new();

        public StopDetector Stops { get; } = new();

        public int? ObservedDelaySec { get; set; }

        public DateTimeOffset? DetectorPositionAt { get; private set; }

        public int? DetectorDelaySec { get; private set; }

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
    }
}

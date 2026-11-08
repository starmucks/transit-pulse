using System.Text.Json;
using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>
/// What the Processor keeps for one partition: the tracks of its vehicles and how far it has read. Kept per partition
/// so a partition's state can leave with it in a rebalance. Not thread-safe: used only by the consume loop.
/// </summary>
/// <param name="schedule">Where accepted positions look up their trips.</param>
internal sealed class PartitionState(CurrentTripSchedule schedule)
{
    private readonly Dictionary<string, VehicleState> _vehicles = [];
    private long? _committedOffset;

    /// <summary>The offset to resume from: one past the last message processed, as Kafka expects a commit to be.</summary>
    public long? NextOffset { get; private set; }

    /// <summary>Whether messages were processed since the last commit; an idle partition has nothing to commit.</summary>
    public bool HasUncommitted => NextOffset != _committedOffset;

    /// <summary>Judges one message and moves the partition past it, whatever the verdict.</summary>
    /// <param name="offset">The message's offset.</param>
    /// <param name="key">The message's key: identifies the vehicle within the system.</param>
    /// <param name="value">The message's value, written by <see cref="PositionMessage.Value"/>.</param>
    /// <returns>The verdict and, for an accepted position, what the trip lookup found and where on the trip's line it lies.</returns>
    public ProcessedMessage Process(long offset, string? key, ReadOnlySpan<byte> value)
    {
        // An unreadable message moves the offset too: it would fail the same way on every retry and block the partition.
        NextOffset = offset + 1;

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

    // A position without a found trip leaves the progress as it was: the vehicle may be back on the same trip next time.
    private ProcessedMessage Accept(VehicleState vehicle, VehiclePosition position)
    {
        var resolution = schedule.Resolve(position, out var trip);
        RouteMatch? route = trip is null ? null : vehicle.Route.Advance(trip, position.Lat, position.Lon, position.ObservedAt);
        return new ProcessedMessage(MessageVerdict.Accepted, resolution, route);
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
        public VehicleTrack Track { get; } = new();

        public RouteProgress Route { get; } = new();
    }
}

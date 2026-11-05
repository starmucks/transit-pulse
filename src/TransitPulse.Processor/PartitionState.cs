using System.Text.Json;
using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>
/// What the Processor keeps for one partition: the tracks of its vehicles and how far it has read. Kept per partition
/// so a partition's state can leave with it in a rebalance. Not thread-safe: used only by the consume loop.
/// </summary>
internal sealed class PartitionState
{
    private readonly Dictionary<string, VehicleTrack> _tracks = [];
    private long? _committedOffset;

    /// <summary>The offset to resume from: one past the last message processed, as Kafka expects a commit to be.</summary>
    public long? NextOffset { get; private set; }

    /// <summary>Whether messages were processed since the last commit; an idle partition has nothing to commit.</summary>
    public bool HasUncommitted => NextOffset != _committedOffset;

    /// <summary>Judges one message and moves the partition past it, whatever the verdict.</summary>
    /// <param name="offset">The message's offset.</param>
    /// <param name="key">The message's key: identifies the vehicle within the system.</param>
    /// <param name="value">The message's value, written by <see cref="PositionMessage.Value"/>.</param>
    /// <returns>Whether the position goes on, or why it was dropped.</returns>
    public MessageVerdict Process(long offset, string? key, ReadOnlySpan<byte> value)
    {
        // An unreadable message moves the offset too: it would fail the same way on every retry and block the partition.
        NextOffset = offset + 1;

        if (key is null || !TryParse(value, out var position))
        {
            return MessageVerdict.Unreadable;
        }

        if (!_tracks.TryGetValue(key, out var track))
        {
            track = new VehicleTrack();
            _tracks.Add(key, track);
        }

        return track.Check(position) switch
        {
            PositionCheck.Accepted => MessageVerdict.Accepted,
            PositionCheck.OutOfOrder => MessageVerdict.OutOfOrder,
            PositionCheck.ImpossibleJump => MessageVerdict.ImpossibleJump,
            var check => throw new InvalidOperationException($"Unknown position check {check}."),
        };
    }

    /// <summary>Records a successful commit.</summary>
    /// <param name="offset">The committed offset: the value <see cref="NextOffset"/> had when the commit was made.</param>
    public void MarkCommitted(long offset) => _committedOffset = offset;

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
}

using System.Text;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Tests;

public sealed class PartitionStateTests
{
    private const string Vehicle = "hsl/40/415";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Message(int seconds, double lat = 60.2) =>
        PositionMessage.Value(new VehiclePosition("hsl", "40/415", null, "1009", lat, 24.9, 8f, null, null, null, _t0.AddSeconds(seconds)));

    [Fact]
    public void A_new_partition_has_nothing_to_commit()
    {
        var state = new PartitionState();

        Assert.Null(state.NextOffset);
        Assert.False(state.HasUncommitted);
    }

    [Fact]
    public void Next_offset_is_one_past_the_processed_message()
    {
        var state = new PartitionState();

        Assert.Equal(MessageVerdict.Accepted, state.Process(41, Vehicle, Message(0)));

        Assert.Equal(42, state.NextOffset);
    }

    [Fact]
    public void Commit_clears_uncommitted_until_the_next_message()
    {
        var state = new PartitionState();
        state.Process(0, Vehicle, Message(0));
        Assert.True(state.HasUncommitted);

        state.MarkCommitted(state.NextOffset!.Value);
        Assert.False(state.HasUncommitted);

        state.Process(1, Vehicle, Message(1));
        Assert.True(state.HasUncommitted);
    }

    [Fact]
    public void Commit_of_an_older_offset_leaves_newer_messages_uncommitted()
    {
        var state = new PartitionState();
        state.Process(0, Vehicle, Message(0));
        var committing = state.NextOffset!.Value;
        state.Process(1, Vehicle, Message(1));

        state.MarkCommitted(committing);

        Assert.True(state.HasUncommitted);
    }

    [Fact]
    public void Dropped_positions_get_their_reason_and_still_move_the_offset()
    {
        var state = new PartitionState();
        state.Process(0, Vehicle, Message(10));

        Assert.Equal(MessageVerdict.OutOfOrder, state.Process(1, Vehicle, Message(5)));
        Assert.Equal(2, state.NextOffset);

        // One degree of latitude (~111 km) in one second.
        Assert.Equal(MessageVerdict.ImpossibleJump, state.Process(2, Vehicle, Message(11, lat: 61.2)));
        Assert.Equal(3, state.NextOffset);
    }

    public static TheoryData<string?, string> UnreadableMessages => new()
    {
        { null, """{"feedId":"hsl"}""" },
        { Vehicle, "not json" },
        { Vehicle, "null" },
        { Vehicle, "" },
        // Fields missing: the position's own rules reject them.
        { Vehicle, """{"feedId":"hsl"}""" },
        { Vehicle, """{"feedId":"hsl","vehicleId":"1","lat":91,"lon":24.9,"observedAt":"2026-11-05T12:00:00Z"}""" },
    };

    [Theory]
    [MemberData(nameof(UnreadableMessages))]
    public void Unreadable_message_is_reported_and_moves_the_offset(string? key, string value)
    {
        var state = new PartitionState();

        var verdict = state.Process(7, key, Encoding.UTF8.GetBytes(value));

        Assert.Equal(MessageVerdict.Unreadable, verdict);
        Assert.Equal(8, state.NextOffset);
    }

    [Fact]
    public void Message_without_a_key_does_not_touch_any_track()
    {
        var state = new PartitionState();
        state.Process(0, Vehicle, Message(10));

        state.Process(1, null, Message(5));

        // Still judged against the time 10: the keyless message was not a position of this vehicle.
        Assert.Equal(MessageVerdict.OutOfOrder, state.Process(2, Vehicle, Message(9)));
        Assert.Equal(MessageVerdict.Accepted, state.Process(3, Vehicle, Message(11)));
    }

    [Fact]
    public void Vehicles_are_judged_separately()
    {
        var state = new PartitionState();
        state.Process(0, Vehicle, Message(10));

        // Earlier than the first vehicle's last position, and far from it: a different vehicle's first position.
        var other = PositionMessage.Value(new VehiclePosition("hsl", "12/7", null, null, 62.0, 25.0, null, null, null, null, _t0));

        Assert.Equal(MessageVerdict.Accepted, state.Process(1, "hsl/12/7", other));
    }
}

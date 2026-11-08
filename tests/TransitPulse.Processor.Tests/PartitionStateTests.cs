using System.Text;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Tests;

public sealed class PartitionStateTests
{
    private const string Vehicle = "hsl/40/415";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Message(int seconds, double lat = 60.2) =>
        PositionMessage.Value(new VehiclePosition("hsl", "40/415", null, "1009", lat, 24.9, 8f, null, null, null, _t0.AddSeconds(seconds)));

    private static byte[] OnTrip(string feed, string tripId, int seconds = 0, double lat = 60.2) =>
        PositionMessage.Value(new VehiclePosition(feed, "40/415", tripId, "1009", lat, 24.9, 8f, null, null, null, _t0.AddSeconds(seconds)));

    private static PartitionState NewState() => new(new CurrentTripSchedule("hsl"));

    private static TripSchedule ScheduleOf(int startHour)
    {
        DateOnly day = new(2026, 11, 5);
        return new([day], [new ScheduledTrip($"1009_{startHour}", "1009", 0, startHour * 3600, day)]);
    }

    [Fact]
    public void Accepted_position_looks_up_its_trip()
    {
        var schedule = new CurrentTripSchedule("hsl");
        schedule.Replace(ScheduleOf(14));
        var state = new PartitionState(schedule);

        var found = state.Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00"));
        var missed = state.Process(1, Vehicle, OnTrip("hsl", "9999/0/20261105/14:00:00", seconds: 1));

        Assert.Equal(new ProcessedMessage(MessageVerdict.Accepted, TripResolution.Found), found);
        Assert.Equal(new ProcessedMessage(MessageVerdict.Accepted, TripResolution.RouteUnknown), missed);
    }

    [Fact]
    public void Position_of_a_parked_vehicle_has_no_descriptor()
    {
        var schedule = new CurrentTripSchedule("hsl");
        schedule.Replace(ScheduleOf(14));

        Assert.Equal(TripResolution.NoDescriptor, new PartitionState(schedule).Process(0, Vehicle, Message(0)).Trip);
    }

    [Fact]
    public void Without_a_schedule_the_position_still_goes_on()
    {
        var result = NewState().Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00"));

        Assert.Equal(new ProcessedMessage(MessageVerdict.Accepted, TripResolution.NoSchedule), result);
    }

    [Fact]
    public void Feed_without_a_schedule_makes_no_lookup()
    {
        // A real Entur trip id: it must not count as a malformed HSL descriptor.
        var result = NewState().Process(0, "entur-sky/1", OnTrip("entur-sky", "SKY:ServiceJourney:6_1234"));

        Assert.Equal(new ProcessedMessage(MessageVerdict.Accepted, null), result);
    }

    [Fact]
    public void Dropped_position_makes_no_lookup()
    {
        var schedule = new CurrentTripSchedule("hsl");
        schedule.Replace(ScheduleOf(14));
        var state = new PartitionState(schedule);
        state.Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00", seconds: 10));

        var old = state.Process(1, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00", seconds: 5));
        var jump = state.Process(2, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00", seconds: 11, lat: 61.2));
        var unreadable = state.Process(3, null, OnTrip("hsl", "1009/0/20261105/14:00:00", seconds: 12));

        Assert.Equal(new ProcessedMessage(MessageVerdict.OutOfOrder, null), old);
        Assert.Equal(new ProcessedMessage(MessageVerdict.ImpossibleJump, null), jump);
        Assert.Equal(new ProcessedMessage(MessageVerdict.Unreadable, null), unreadable);
    }

    [Fact]
    public void Replaced_schedule_applies_from_the_next_message()
    {
        var schedule = new CurrentTripSchedule("hsl");
        schedule.Replace(ScheduleOf(14));
        var state = new PartitionState(schedule);
        var before = state.Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/15:00:00"));

        schedule.Replace(ScheduleOf(15));
        var after = state.Process(1, Vehicle, OnTrip("hsl", "1009/0/20261105/15:00:00", seconds: 1));

        Assert.Equal(TripResolution.NoDeparture, before.Trip);
        Assert.Equal(TripResolution.Found, after.Trip);
    }

    [Fact]
    public void A_new_partition_has_nothing_to_commit()
    {
        var state = NewState();

        Assert.Null(state.NextOffset);
        Assert.False(state.HasUncommitted);
    }

    [Fact]
    public void Next_offset_is_one_past_the_processed_message()
    {
        var state = NewState();

        Assert.Equal(MessageVerdict.Accepted, state.Process(41, Vehicle, Message(0)).Verdict);

        Assert.Equal(42, state.NextOffset);
    }

    [Fact]
    public void Commit_clears_uncommitted_until_the_next_message()
    {
        var state = NewState();
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
        var state = NewState();
        state.Process(0, Vehicle, Message(0));
        var committing = state.NextOffset!.Value;
        state.Process(1, Vehicle, Message(1));

        state.MarkCommitted(committing);

        Assert.True(state.HasUncommitted);
    }

    [Fact]
    public void Dropped_positions_get_their_reason_and_still_move_the_offset()
    {
        var state = NewState();
        state.Process(0, Vehicle, Message(10));

        Assert.Equal(MessageVerdict.OutOfOrder, state.Process(1, Vehicle, Message(5)).Verdict);
        Assert.Equal(2, state.NextOffset);

        // One degree of latitude (~111 km) in one second.
        Assert.Equal(MessageVerdict.ImpossibleJump, state.Process(2, Vehicle, Message(11, lat: 61.2)).Verdict);
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
        var state = NewState();

        var verdict = state.Process(7, key, Encoding.UTF8.GetBytes(value)).Verdict;

        Assert.Equal(MessageVerdict.Unreadable, verdict);
        Assert.Equal(8, state.NextOffset);
    }

    [Fact]
    public void Message_without_a_key_does_not_touch_any_track()
    {
        var state = NewState();
        state.Process(0, Vehicle, Message(10));

        state.Process(1, null, Message(5));

        // Still judged against the time 10: the keyless message was not a position of this vehicle.
        Assert.Equal(MessageVerdict.OutOfOrder, state.Process(2, Vehicle, Message(9)).Verdict);
        Assert.Equal(MessageVerdict.Accepted, state.Process(3, Vehicle, Message(11)).Verdict);
    }

    [Fact]
    public void Vehicles_are_judged_separately()
    {
        var state = NewState();
        state.Process(0, Vehicle, Message(10));

        // Earlier than the first vehicle's last position, and far from it: a different vehicle's first position.
        var other = PositionMessage.Value(new VehiclePosition("hsl", "12/7", null, null, 62.0, 25.0, null, null, null, null, _t0));

        Assert.Equal(MessageVerdict.Accepted, state.Process(1, "hsl/12/7", other).Verdict);
    }
}

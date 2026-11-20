using System.Text;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Tests;

public sealed class PartitionStateTests
{
    private const string Vehicle = "hsl/40/415";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Message(int seconds, double lat = 60.2) =>
        PositionMessage.Value(new VehiclePosition("hsl", "40/415", null, "1009", lat, 24.9, 8f, null, null, null, _t0.AddSeconds(seconds)));

    private static byte[] OnTrip(string feed, string tripId, int seconds = 0, double lat = 60.2, int? delaySec = null) =>
        PositionMessage.Value(new VehiclePosition(feed, "40/415", tripId, "1009", lat, 24.9, 8f, null, delaySec, null, _t0.AddSeconds(seconds)));

    private static PartitionState NewState() => new(new CurrentTripSchedule("hsl", TimeZoneInfo.Utc));

    private static TripSchedule ScheduleOf(
        int startHour, RouteShape? shape = null, StopPattern? stops = null, StopTimetable? timetable = null)
    {
        DateOnly day = new(2026, 11, 5);
        return new([day], [new ScheduledTrip($"1009_{startHour}", "1009", 0, startHour * 3600, day, shape, stops, timetable)]);
    }

    // A line running east along a parallel; the messages lie at 60.2° N, 24.9° E.
    private static RouteShape LineAt(double lat) => new([lat, lat], [24.8, 25.0]);

    [Fact]
    public void Accepted_position_looks_up_its_trip()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14));
        var state = new PartitionState(schedule);

        var found = state.Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00"));
        var missed = state.Process(1, Vehicle, OnTrip("hsl", "9999/0/20261105/14:00:00", seconds: 1));

        Assert.Equal(TripResolution.Found, found.Trip);
        Assert.Equal(new RouteMatch(RouteMatchStatus.NoShape, 0, 0), found.Route);
        Assert.Equal("1009_14", found.Run!.TripId);
        Assert.Equal("1009_14", found.Position!.Schedule!.TripId);
        Assert.Equal(TripResolution.RouteUnknown, missed.Trip);
        Assert.Null(missed.Position!.Schedule);
    }

    [Fact]
    public void Position_of_a_found_trip_is_placed_on_its_line()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14, LineAt(60.2)));

        var route = new PartitionState(schedule).Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00")).Route!.Value;

        // 0.1° of longitude at 60.2° N.
        Assert.Equal(RouteMatchStatus.OnRoute, route.Status);
        Assert.Equal(5_526, route.AlongMetres, tolerance: 10);
        Assert.Equal(0, route.OffsetMetres, tolerance: 0.5);
    }

    [Fact]
    public void Progress_on_the_line_is_kept_across_messages_and_a_position_without_a_trip_leaves_it()
    {
        // A line running north along the messages' meridian.
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14, new RouteShape([60.1, 60.3], [24.9, 24.9])));
        var state = new PartitionState(schedule);

        var first = state.Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00", seconds: 0, lat: 60.201)).Route!.Value;
        var parked = state.Process(1, Vehicle, Message(1, lat: 60.2009));

        // 22 m back is GPS noise for a vehicle with progress; a vehicle starting over would be placed there.
        var next = state.Process(2, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00", seconds: 2, lat: 60.2008)).Route!.Value;

        Assert.Null(parked.Route);
        Assert.Equal(RouteMatchStatus.OnRoute, next.Status);
        Assert.Equal(first.AlongMetres, next.AlongMetres);
    }

    [Fact]
    public void Standing_at_a_stop_of_the_trip_gives_its_arrival_and_departure_once_each()
    {
        // A line running north along the messages' meridian, one stop on it at 60.2° N; 0.0001° of latitude is ~11 m.
        var line = new RouteShape([60.1, 60.3], [24.9, 24.9]);
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14, line, new StopPattern(line, [new StopPoint("1020453", 1, 60.2, 24.9)])));
        var state = new PartitionState(schedule);
        const string trip = "1009/0/20261105/14:00:00";

        // 56 m before the stop, 17 m before it (in its zone), standing there, 67 m past it, then a position without a trip.
        (int Seconds, double Lat, string? Trip)[] drive =
            [(0, 60.1995, trip), (5, 60.19985, trip), (6, 60.19985, trip), (7, 60.19985, trip), (10, 60.2006, trip), (11, 60.2007, null)];
        var found = drive
            .Select((p, offset) =>
            {
                var message = p.Trip is null ? Message(p.Seconds, p.Lat) : OnTrip("hsl", p.Trip, p.Seconds, p.Lat);
                state.Process(offset, Vehicle, message);
                return state.StopEvents.ToList();
            })
            .ToList();

        Assert.Equal([0, 0, 1, 0, 1, 0], found.Select(events => events.Count));
        var arrival = found[2][0];
        Assert.Equal(StopEventKind.Arrival, arrival.Kind);
        Assert.Equal("hsl/40/415/1009_14/2026-11-05/1/arrival", arrival.Id);
        Assert.Equal(_t0.AddSeconds(5), arrival.At);
        Assert.Equal("hsl/40/415/1009_14/2026-11-05/1/departure", found[4][0].Id);
    }

    [Fact]
    public void Actual_arrival_updates_effective_delay_and_compares_it_with_the_source()
    {
        var line = new RouteShape([60.1, 60.3], [24.9, 24.9]);
        var stops = new StopPattern(line, [new StopPoint("1020453", 1, 60.2, 24.9)]);
        var timetable = new StopTimetable([new ScheduledStopTime(1, 12 * 3600, 12 * 3600)]);
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(12, line, stops, timetable));
        var state = new PartitionState(schedule);
        const string trip = "1009/0/20261105/12:00:00";

        state.Process(0, Vehicle, OnTrip("hsl", trip, 0, 60.1995, delaySec: 10));
        state.Process(1, Vehicle, OnTrip("hsl", trip, 5, 60.19985, delaySec: 12));
        var arrived = state.Process(2, Vehicle, OnTrip("hsl", trip, 6, 60.19985, delaySec: 13));
        var after = state.Process(3, Vehicle, OnTrip("hsl", trip, 7, 60.19985));

        Assert.Equal(new DelayCheck(DelayCheckResult.Compared, CalculatedSec: 5, SourceSec: 12), arrived.Delay);
        Assert.Equal(13, arrived.Position!.DelaySec);
        Assert.Equal(5, after.Position!.DelaySec);
        Assert.Equal("1020453", arrived.Position.Schedule!.NextStop!.StopId);
        Assert.Equal(12 * 3600, arrived.Position.Schedule.NextStop.ArrivalSeconds);
    }

    [Fact]
    public void Position_far_from_its_trip_line_is_off_route()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14, LineAt(60.25)));

        var route = new PartitionState(schedule).Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00")).Route!.Value;

        Assert.Equal(RouteMatchStatus.OffRoute, route.Status);
        Assert.Equal(5_560, route.OffsetMetres, tolerance: 10);
    }

    [Fact]
    public void Position_of_a_parked_vehicle_has_no_descriptor()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14));

        Assert.Equal(TripResolution.NoDescriptor, new PartitionState(schedule).Process(0, Vehicle, Message(0)).Trip);
    }

    [Fact]
    public void Without_a_schedule_the_position_still_goes_on()
    {
        var result = NewState().Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00"));

        Assert.Equal(TripResolution.NoSchedule, result.Trip);
        Assert.NotNull(result.Position);
        Assert.Null(result.Position.Schedule);
    }

    [Fact]
    public void Feed_without_a_schedule_makes_no_lookup()
    {
        // A real Entur trip id: it must not count as a malformed HSL descriptor.
        var result = NewState().Process(0, "entur-sky/1", OnTrip("entur-sky", "SKY:ServiceJourney:6_1234"));

        Assert.Null(result.Trip);
        Assert.NotNull(result.Position);
        Assert.Null(result.Position.Schedule);
    }

    [Fact]
    public void Dropped_position_makes_no_lookup()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
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
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
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

    [Fact]
    public void Snapshot_round_trip_rebinds_stop_and_delay_state_to_a_reloaded_schedule()
    {
        var line = new RouteShape([60.1, 60.3], [24.9, 24.9]);
        var stops = new StopPattern(line, [new StopPoint("1020453", 1, 60.2, 24.9)]);
        var timetable = new StopTimetable([new ScheduledStopTime(1, 12 * 3600, 12 * 3600)]);
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(12, line, stops, timetable));
        var state = new PartitionState(schedule);
        const string trip = "1009/0/20261105/12:00:00";
        state.Process(0, Vehicle, OnTrip("hsl", trip, 0, 60.1995, delaySec: 10));
        state.Process(1, Vehicle, OnTrip("hsl", trip, 5, 60.19985, delaySec: 12));
        state.Process(2, Vehicle, OnTrip("hsl", trip, 6, 60.19985, delaySec: 13));

        var reloadedLine = new RouteShape([60.1, 60.3], [24.9, 24.9]);
        var reloadedStops = new StopPattern(reloadedLine, [new StopPoint("1020453", 1, 60.2, 24.9)]);
        var reloadedTimes = new StopTimetable([new ScheduledStopTime(1, 12 * 3600, 12 * 3600)]);
        schedule.Replace(ScheduleOf(12, reloadedLine, reloadedStops, reloadedTimes));
        var restored = PartitionState.Restore(state.Capture(), schedule);

        var standing = restored.Process(3, Vehicle, OnTrip("hsl", trip, 7, 60.19985));
        var departed = restored.Process(4, Vehicle, OnTrip("hsl", trip, 10, 60.2006));

        Assert.Equal(3, state.NextOffset);
        Assert.Equal(5, standing.Position!.DelaySec);
        Assert.Single(restored.StopEvents);
        Assert.Equal(StopEventKind.Departure, restored.StopEvents[0].Kind);
        Assert.Equal(5, departed.Position!.DelaySec);
    }

    [Fact]
    public void Restored_checkpoint_offset_is_not_uncommitted()
    {
        var state = NewState();
        state.Process(41, Vehicle, Message(0));

        var restored = PartitionState.Restore(state.Capture(), new CurrentTripSchedule("hsl", TimeZoneInfo.Utc));

        Assert.Equal(42, restored.NextOffset);
        // Redis can be newer than Kafka when the previous process died between the checkpoint and the offset commit.
        Assert.True(restored.HasUncommitted);
    }

    [Fact]
    public void Snapshot_round_trip_keeps_vehicle_keys_separate()
    {
        var state = NewState();
        state.Process(0, Vehicle, Message(10));
        state.Process(1, "hsl/12/7", Message(20));

        var restored = PartitionState.Restore(state.Capture(), new CurrentTripSchedule("hsl", TimeZoneInfo.Utc));

        Assert.Equal(MessageVerdict.OutOfOrder, restored.Process(2, Vehicle, Message(9)).Verdict);
        Assert.Equal(MessageVerdict.OutOfOrder, restored.Process(3, "hsl/12/7", Message(19)).Verdict);
    }

    [Fact]
    public void Active_schedule_state_cannot_restore_without_its_trip()
    {
        var line = new RouteShape([60.1, 60.3], [24.9, 24.9]);
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(ScheduleOf(14, line, new StopPattern(line, [new StopPoint("A", 1, 60.2, 24.9)])));
        var state = new PartitionState(schedule);
        state.Process(0, Vehicle, OnTrip("hsl", "1009/0/20261105/14:00:00"));
        schedule.Replace(ScheduleOf(15));

        Assert.Throws<InvalidOperationException>(() => PartitionState.Restore(state.Capture(), schedule));
    }
}

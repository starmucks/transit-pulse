using System.Text.Json;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Tests;

public sealed class ProcessorCheckpointTests
{
    private static readonly DateOnly _day = new(2026, 11, 4);
    private static readonly DateTimeOffset _t0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Round_trip_restores_partitions_offsets_and_bunching_ownership()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        var partitions = new Dictionary<int, PartitionState>
        {
            [0] = Partition(schedule, 3, "hsl/leader", Position("leader", 0)),
            [1] = Partition(schedule, 7, "hsl/follower", Position("follower", 0)),
        };
        var ownership = new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>
        {
            [0] = [("hsl", "leader")],
            [1] = [("hsl", "follower")],
        };

        var restored = ProcessorCheckpoint.Restore(
            ProcessorCheckpoint.Capture(partitions, new BunchingEventTimeBuffer(TimeZoneInfo.Utc), ownership), schedule);

        Assert.Equal(4, restored.Partitions[0].NextOffset);
        Assert.Equal(8, restored.Partitions[1].NextOffset);
        Assert.True(restored.Partitions[0].HasUncommitted);
        Assert.Contains(("hsl", "leader"), restored.BunchingVehicles[0]);
        Assert.Contains(("hsl", "follower"), restored.BunchingVehicles[1]);
    }

    [Fact]
    public void Detector_vehicle_without_partition_ownership_is_rejected()
    {
        var state = ScheduledState();
        state.Ownership[1].Clear();

        Assert.Throws<ArgumentException>(() =>
            ProcessorCheckpoint.Capture(state.Partitions, state.Buffer, state.Ownership));
    }

    [Fact]
    public void Duplicate_partition_is_rejected()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        var state = Partition(schedule, 0, "hsl/leader", Position("leader", 0));
        var saved = ProcessorCheckpoint.Capture(
            new Dictionary<int, PartitionState> { [0] = state },
            new BunchingEventTimeBuffer(TimeZoneInfo.Utc),
            new Dictionary<int, HashSet<(string FeedId, string VehicleId)>> { [0] = [("hsl", "leader")] });
        saved = saved with { Partitions = [saved.Partitions[0], saved.Partitions[0]] };

        Assert.Throws<ArgumentException>(() => ProcessorCheckpoint.Restore(saved, schedule));
    }

    [Fact]
    public void Duplicate_vehicle_ownership_is_rejected()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        var partitions = new Dictionary<int, PartitionState>
        {
            [0] = Partition(schedule, 0, "hsl/leader", Position("leader", 0)),
            [1] = Partition(schedule, 1, "hsl/leader", Position("leader", 1)),
        };
        var saved = ProcessorCheckpoint.Capture(
            partitions,
            new BunchingEventTimeBuffer(TimeZoneInfo.Utc),
            new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>
            {
                [0] = [("hsl", "leader")],
                [1] = [],
            });
        saved = saved with
        {
            BunchingOwnership =
            [
                new ProcessorBunchingOwnershipSnapshot(0, [new ProcessorVehicleKeySnapshot("hsl", "leader")]),
                new ProcessorBunchingOwnershipSnapshot(1, [new ProcessorVehicleKeySnapshot("hsl", "leader")]),
            ],
        };

        Assert.Throws<ArgumentException>(() => ProcessorCheckpoint.Restore(saved, schedule));
    }

    [Fact]
    public void Unknown_schema_version_is_rejected()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        var saved = ProcessorCheckpoint.Capture(
            new Dictionary<int, PartitionState>(),
            new BunchingEventTimeBuffer(TimeZoneInfo.Utc),
            new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>());

        Assert.Throws<ArgumentException>(() =>
            ProcessorCheckpoint.Restore(saved with { Version = ProcessorCheckpoint.CurrentVersion + 1 }, schedule));
    }

    [Fact]
    public void Scheduled_state_cannot_restore_without_the_current_schedule()
    {
        var state = ScheduledState();
        var saved = ProcessorCheckpoint.Capture(state.Partitions, state.Buffer, state.Ownership);

        Assert.Throws<InvalidOperationException>(() =>
            ProcessorCheckpoint.Restore(saved, new CurrentTripSchedule("hsl", TimeZoneInfo.Utc)));
    }

    [Fact]
    public void RestoreOwned_keeps_only_partitions_this_instance_owns()
    {
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        var partitions = new Dictionary<int, PartitionState>
        {
            [0] = Partition(schedule, 3, "hsl/leader", Position("leader", 0)),
            [1] = Partition(schedule, 7, "hsl/follower", Position("follower", 0)),
        };
        var ownership = new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>
        {
            [0] = [("hsl", "leader")],
            [1] = [("hsl", "follower")],
        };
        var saved = ProcessorCheckpoint.Capture(partitions, new BunchingEventTimeBuffer(TimeZoneInfo.Utc), ownership);

        var restored = ProcessorCheckpoint.RestoreOwned(saved, schedule, new HashSet<int> { 1 });

        Assert.False(restored.Partitions.ContainsKey(0));
        Assert.Equal(8, restored.Partitions[1].NextOffset);
        Assert.False(restored.BunchingVehicles.ContainsKey(0));
        Assert.Contains(("hsl", "follower"), restored.BunchingVehicles[1]);
    }

    [Fact]
    public void Serialized_checkpoint_round_trip_preserves_all_state()
    {
        var state = ScheduledState();
        var saved = ProcessorCheckpoint.Capture(state.Partitions, state.Buffer, state.Ownership);
        var payload = ProcessorCheckpointSerializer.Serialize(saved);

        var restored = ProcessorCheckpoint.Restore(ProcessorCheckpointSerializer.Deserialize(payload), state.Schedule);
        var recaptured = ProcessorCheckpoint.Capture(
            restored.Partitions,
            restored.Bunching,
            restored.BunchingVehicles);

        Assert.Equal(payload, ProcessorCheckpointSerializer.Serialize(recaptured));
    }

    [Fact]
    public void Corrupt_serialized_checkpoint_is_rejected()
    {
        Assert.Throws<JsonException>(() => ProcessorCheckpointSerializer.Deserialize("{not-json"u8));
    }

    private static PartitionState Partition(
        CurrentTripSchedule schedule,
        long offset,
        string key,
        VehiclePosition position)
    {
        var state = new PartitionState(schedule);
        state.Process(offset, key, PositionMessage.Value(position));
        return state;
    }

    private static VehiclePosition Position(string vehicle, int seconds) => new(
        "hsl", vehicle, null, "1009", 60.2, 24.9, 0, null, null, null, _t0.AddSeconds(seconds));

    private static (CurrentTripSchedule Schedule,
        Dictionary<int, PartitionState> Partitions,
        BunchingEventTimeBuffer Buffer,
        Dictionary<int, HashSet<(string FeedId, string VehicleId)>> Ownership) ScheduledState()
    {
        var shape = new RouteShape([60.2, 60.2], [24.9, 24.918]);
        var pattern = new StopPattern(
            shape,
            [new StopPoint("start", 1, 60.2, 24.9), new StopPoint("end", 2, 60.2, 24.918)]);
        var leader = Trip(shape, pattern, "leader-trip", 0);
        var follower = Trip(shape, pattern, "follower-trip", 600);
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(new TripSchedule([_day], [leader, follower]));
        var partitions = new Dictionary<int, PartitionState>
        {
            [0] = Partition(schedule, 0, "hsl/leader", Position("leader", 0)),
            [1] = Partition(schedule, 0, "hsl/follower", Position("follower", 0)),
        };
        var buffer = new BunchingEventTimeBuffer(TimeZoneInfo.Utc);
        buffer.Assign([0, 1]);
        buffer.Observe(0, Position("leader", 0), _t0.AddHours(1), leader, pattern.Calls[^1].AlongMetres * 0.06, []);
        buffer.Observe(1, Position("follower", 0), _t0.AddHours(1), follower, 0, []);
        buffer.ReachedEnd(0, []);
        buffer.ReachedEnd(1, []);
        var ownership = new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>
        {
            [0] = [("hsl", "leader")],
            [1] = [("hsl", "follower")],
        };
        return (schedule, partitions, buffer, ownership);
    }

    private static ScheduledTrip Trip(RouteShape shape, StopPattern pattern, string tripId, int startSeconds) => new(
        tripId,
        "1009",
        0,
        startSeconds,
        _day,
        shape,
        pattern,
        new StopTimetable(
        [
            new ScheduledStopTime(1, startSeconds, startSeconds),
            new ScheduledStopTime(2, startSeconds + 1000, startSeconds + 1000),
        ]));
}

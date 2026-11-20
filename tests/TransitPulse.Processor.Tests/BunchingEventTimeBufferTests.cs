using TransitPulse.Domain;

namespace TransitPulse.Processor.Tests;

public sealed class BunchingEventTimeBufferTests
{
    private static readonly DateOnly _day = new(2026, 11, 4);
    private static readonly DateTimeOffset _t0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Vehicle_blocks_from_different_partitions_are_restored_to_event_time_order()
    {
        var fixture = new Fixture();
        var buffer = new BunchingEventTimeBuffer(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();
        buffer.Assign([0, 1, 2]);
        buffer.ReachedEnd(2, found);

        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            Fixture.Observe(buffer, found, partition: 0, vehicle: "leader", fixture.Leader, seconds, fixture.Length * 0.06);
        }

        Assert.Empty(found);
        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            Fixture.Observe(buffer, found, partition: 1, vehicle: "follower", fixture.Follower, seconds, alongMetres: 0);
        }

        var bunching = Assert.Single(found);
        Assert.Equal(_t0, bunching.StartedAt);
        Assert.Equal(_t0.AddSeconds(30), bunching.ConfirmedAt);
    }

    [Fact]
    public void A_source_timestamp_ahead_of_receipt_does_not_poison_the_watermark()
    {
        var fixture = new Fixture();
        var buffer = new BunchingEventTimeBuffer(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();
        buffer.Assign([0, 1]);
        var future = Fixture.Position("bad-clock", _t0.AddHours(1));

        buffer.Observe(0, future, _t0, null, null, found);
        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            Fixture.Observe(buffer, found, partition: 0, vehicle: "leader", fixture.Leader, seconds, fixture.Length * 0.06);
        }

        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            Fixture.Observe(buffer, found, partition: 1, vehicle: "follower", fixture.Follower, seconds, alongMetres: 0);
        }

        Assert.Single(found);
    }

    [Fact]
    public void Snapshot_round_trip_keeps_pending_order_and_partition_end()
    {
        var fixture = new Fixture();
        var buffer = new BunchingEventTimeBuffer(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();
        buffer.Assign([0, 1, 2]);
        buffer.ReachedEnd(2, found);
        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            Fixture.Observe(buffer, found, partition: 0, vehicle: "leader", fixture.Leader, seconds, fixture.Length * 0.06);
        }

        var reloaded = new Fixture();
        var restored = BunchingEventTimeBuffer.Restore(buffer.Capture(), reloaded.Schedule(), TimeZoneInfo.Utc);
        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            Fixture.Observe(restored, found, partition: 1, vehicle: "follower", reloaded.Follower, seconds, alongMetres: 0);
        }

        var bunching = Assert.Single(found);
        Assert.Equal(_t0, bunching.StartedAt);
        Assert.Equal(_t0.AddSeconds(30), bunching.ConfirmedAt);
    }

    [Fact]
    public void Capture_discards_pending_observations_from_an_old_generation()
    {
        var fixture = new Fixture();
        var buffer = new BunchingEventTimeBuffer(TimeZoneInfo.Utc);
        buffer.Assign([0, 1]);
        Fixture.Observe(buffer, [], partition: 0, vehicle: "leader", fixture.Leader, 0, fixture.Length * 0.06);

        buffer.Remove([0], []);
        buffer.Assign([0]);

        Assert.Empty(buffer.Capture().Pending);
    }

    [Fact]
    public void Pending_snapshot_cannot_restore_without_its_trip()
    {
        var fixture = new Fixture();
        var buffer = new BunchingEventTimeBuffer(TimeZoneInfo.Utc);
        buffer.Assign([0, 1]);
        Fixture.Observe(buffer, [], partition: 0, vehicle: "leader", fixture.Leader, 0, fixture.Length * 0.06);
        var incomplete = new TripSchedule([_day], [fixture.Follower]);

        Assert.Throws<InvalidOperationException>(() =>
            BunchingEventTimeBuffer.Restore(buffer.Capture(), incomplete, TimeZoneInfo.Utc));
    }

    private sealed class Fixture
    {
        private readonly RouteShape _shape = new([60.2, 60.2], [24.9, 24.918]);

        public Fixture()
        {
            var pattern = new StopPattern(
                _shape,
                [new StopPoint("start", 1, 60.2, 24.9), new StopPoint("end", 2, 60.2, 24.918)]);
            Length = pattern.Calls[^1].AlongMetres;
            Leader = Trip(pattern, "leader-trip", 0);
            Follower = Trip(pattern, "follower-trip", 600);
        }

        public double Length { get; }

        public ScheduledTrip Leader { get; }

        public ScheduledTrip Follower { get; }

        public TripSchedule Schedule() => new([_day], [Leader, Follower]);

        public static void Observe(
            BunchingEventTimeBuffer buffer,
            ICollection<BunchingEvent> events,
            int partition,
            string vehicle,
            ScheduledTrip trip,
            int seconds,
            double alongMetres)
        {
            var position = Position(vehicle, _t0.AddSeconds(seconds));
            buffer.Observe(partition, position, _t0.AddHours(1), trip, alongMetres, events);
        }

        public static VehiclePosition Position(string vehicle, DateTimeOffset at) => new(
            "hsl", vehicle, null, "1009", 60.2, 24.9, 0, null, null, null, at);

        private ScheduledTrip Trip(StopPattern pattern, string tripId, int startSeconds) => new(
            tripId,
            "1009",
            0,
            startSeconds,
            _day,
            _shape,
            pattern,
            new StopTimetable(
            [
                new ScheduledStopTime(1, startSeconds, startSeconds),
                new ScheduledStopTime(2, startSeconds + 1000, startSeconds + 1000),
            ]));
    }
}

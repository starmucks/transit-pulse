namespace TransitPulse.Domain.Tests;

public sealed class BunchingDetectorTests
{
    private static readonly DateOnly _day = new(2026, 11, 4);
    private static readonly DateTimeOffset _t0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Vehicles_converge_and_separate_as_one_episode()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();

        fixture.AdvancePair(detector, found, 0, actualRatio: 1);
        for (var seconds = 5; seconds <= 35; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        fixture.AdvancePair(detector, found, 40, actualRatio: 0.10);
        fixture.AdvancePair(detector, found, 45, actualRatio: 0.10);
        for (var seconds = 50; seconds <= 80; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 1);
        }

        var bunching = Assert.Single(found);
        Assert.Equal(AnomalyKind.Bunching, bunching.Kind);
        Assert.Equal("follower", bunching.FollowerVehicleId);
        Assert.Equal("leader", bunching.LeaderVehicleId);
        Assert.Equal(_t0.AddSeconds(5), bunching.StartedAt);
        Assert.Equal(_t0.AddSeconds(35), bunching.ConfirmedAt);
        Assert.InRange(bunching.HeadwayRatio, 0.099, 0.101);
        Assert.Equal(600, bunching.ScheduledHeadwaySec, precision: 6);
    }

    [Fact]
    public void A_short_close_pass_does_not_open_an_episode()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();

        for (var seconds = 0; seconds <= 25; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        fixture.AdvancePair(detector, found, 29, actualRatio: 1);

        Assert.Empty(found);
    }

    [Fact]
    public void Recovery_must_last_before_the_same_pair_can_open_again()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();

        for (var seconds = 0; seconds <= 30; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        fixture.AdvancePair(detector, found, 35, actualRatio: 1);
        fixture.AdvancePair(detector, found, 55, actualRatio: 1);
        fixture.AdvancePair(detector, found, 56, actualRatio: 0.10);
        fixture.AdvancePair(detector, found, 90, actualRatio: 0.10);

        Assert.Single(found);

        for (var seconds = 95; seconds <= 125; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 1);
        }

        for (var seconds = 130; seconds <= 160; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Trips_with_different_stop_patterns_are_not_compared()
    {
        var first = new Fixture();
        var second = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();

        for (var seconds = 0; seconds <= 35; seconds += 5)
        {
            detector.Advance(Fixture.Position("follower", seconds), first.Follower, 0, found);
            detector.Advance(Fixture.Position("leader", seconds), second.Leader, second.Length * 0.10, found);
        }

        Assert.Empty(found);
    }

    [Fact]
    public void Removing_a_vehicle_discards_its_active_episode()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();

        for (var seconds = 0; seconds <= 30; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        detector.Remove("hsl", "leader");
        for (var seconds = 35; seconds <= 70; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Snapshot_round_trip_keeps_confirmation_time_across_a_reloaded_schedule()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var found = new List<BunchingEvent>();
        for (var seconds = 0; seconds <= 20; seconds += 5)
        {
            fixture.AdvancePair(detector, found, seconds, actualRatio: 0.10);
        }

        var reloaded = new Fixture();
        var restored = BunchingDetector.Restore(detector.Capture(), reloaded.Schedule(), TimeZoneInfo.Utc);
        for (var seconds = 25; seconds <= 30; seconds += 5)
        {
            reloaded.AdvancePair(restored, found, seconds, actualRatio: 0.10);
        }

        var bunching = Assert.Single(found);
        Assert.Equal(_t0, bunching.StartedAt);
        Assert.Equal(_t0.AddSeconds(30), bunching.ConfirmedAt);
    }

    [Fact]
    public void Snapshot_round_trip_keeps_active_recovery_time()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        var before = new List<BunchingEvent>();
        for (var seconds = 0; seconds <= 30; seconds += 5)
        {
            fixture.AdvancePair(detector, before, seconds, actualRatio: 0.10);
        }

        fixture.AdvancePair(detector, before, 35, actualRatio: 1);
        fixture.AdvancePair(detector, before, 45, actualRatio: 1);
        var reloaded = new Fixture();
        var restored = BunchingDetector.Restore(detector.Capture(), reloaded.Schedule(), TimeZoneInfo.Utc);
        var after = new List<BunchingEvent>();
        reloaded.AdvancePair(restored, after, 65, actualRatio: 1);
        for (var seconds = 70; seconds <= 100; seconds += 5)
        {
            reloaded.AdvancePair(restored, after, seconds, actualRatio: 0.10);
        }

        Assert.Single(before);
        var reopened = Assert.Single(after);
        Assert.Equal(_t0.AddSeconds(70), reopened.StartedAt);
        Assert.Equal(_t0.AddSeconds(100), reopened.ConfirmedAt);
    }

    [Fact]
    public void Active_snapshot_cannot_restore_without_every_trip()
    {
        var fixture = new Fixture();
        var detector = new BunchingDetector(TimeZoneInfo.Utc);
        fixture.AdvancePair(detector, [], 0, actualRatio: 0.10);
        var incomplete = new TripSchedule([_day], [fixture.Follower]);

        Assert.Throws<InvalidOperationException>(() =>
            BunchingDetector.Restore(detector.Capture(), incomplete, TimeZoneInfo.Utc));
    }

    private sealed class Fixture
    {
        private readonly RouteShape _shape = new([60.2, 60.2], [24.9, 24.918]);

        public Fixture()
        {
            Pattern = new StopPattern(
                _shape,
                [new StopPoint("start", 1, 60.2, 24.9), new StopPoint("end", 2, 60.2, 24.918)]);
            Length = Pattern.Calls[^1].AlongMetres;
            Leader = Trip("leader-trip", 0);
            Follower = Trip("follower-trip", 600);
        }

        public StopPattern Pattern { get; }

        public double Length { get; }

        public ScheduledTrip Leader { get; }

        public ScheduledTrip Follower { get; }

        public TripSchedule Schedule() => new([_day], [Leader, Follower]);

        public void AdvancePair(
            BunchingDetector detector,
            ICollection<BunchingEvent> events,
            int seconds,
            double actualRatio)
        {
            detector.Advance(Position("leader", seconds), Leader, Length * actualRatio * 0.6, events);
            detector.Advance(Position("follower", seconds), Follower, 0, events);
        }

        public static VehiclePosition Position(string vehicleId, int seconds) => new(
            "hsl",
            vehicleId,
            null,
            "1009",
            60.2,
            24.9,
            0,
            null,
            null,
            null,
            _t0.AddSeconds(seconds));

        private ScheduledTrip Trip(string tripId, int startSeconds) => new(
            tripId,
            "1009",
            0,
            startSeconds,
            _day,
            _shape,
            Pattern,
            new StopTimetable(
            [
                new ScheduledStopTime(1, startSeconds, startSeconds),
                new ScheduledStopTime(2, startSeconds + 1000, startSeconds + 1000),
            ]));
    }
}

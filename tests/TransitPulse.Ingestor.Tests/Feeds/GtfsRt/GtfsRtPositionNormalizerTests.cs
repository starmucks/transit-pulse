using Google.Protobuf;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Feeds.GtfsRt;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Normalization;
using Rt = TransitRealtime;

namespace TransitPulse.Ingestor.Tests.Feeds.GtfsRt;

public sealed class GtfsRtPositionNormalizerTests
{
    private static readonly DateTimeOffset _observedAt = DateTimeOffset.FromUnixTimeSeconds(1_792_972_781);

    // Received 3 s after the measurement, the measured median age of an MBTA snapshot.
    private static readonly DateTimeOffset _receivedAt = _observedAt.AddSeconds(3);

    // A real record (bus 28, Boston, 2026-10-26); each test changes only the fields it is about.
    private static Rt.VehiclePosition Sample() => new()
    {
        Trip = new Rt.TripDescriptor
        {
            TripId = "78210003",
            RouteId = "28",
            DirectionId = 1,
            StartTime = "19:44:00",
            StartDate = "20261025",
            ScheduleRelationship = Rt.TripDescriptor.Types.ScheduleRelationship.Scheduled,
        },
        Position = new Rt.Position { Latitude = 42.2943192f, Longitude = -71.0880432f, Bearing = 0 },
        CurrentStopSequence = 14,
        CurrentStatus = Rt.VehiclePosition.Types.VehicleStopStatus.StoppedAt,
        Timestamp = (ulong)_observedAt.ToUnixTimeSeconds(),
        StopId = "1736",
        Vehicle = new Rt.VehicleDescriptor { Id = "y1256", Label = "1256" },
        OccupancyStatus = Rt.VehiclePosition.Types.OccupancyStatus.ManySeatsAvailable,
        OccupancyPercentage = 20,
    };

    private static Rt.VehiclePosition Sample(Action<Rt.VehiclePosition> change)
    {
        var vehicle = Sample();
        change(vehicle);
        return vehicle;
    }

    private static IReadOnlyList<VehiclePosition> Normalize(DateTimeOffset receivedAt, params Rt.FeedEntity[] entities) =>
        Normalize(new DropLog(), receivedAt, entities);

    private static IReadOnlyList<VehiclePosition> Normalize(DropLog drops, DateTimeOffset receivedAt, params Rt.FeedEntity[] entities)
    {
        var message = new Rt.FeedMessage
        {
            Header = new Rt.FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)receivedAt.ToUnixTimeSeconds() },
        };
        message.Entity.Add(entities);
        return Normalize(message.ToByteArray(), receivedAt, drops);
    }

    private static IReadOnlyList<VehiclePosition> Normalize(byte[] payload, DateTimeOffset receivedAt, DropLog? drops = null) =>
        new GtfsRtPositionNormalizer("mbta", "mbta-vp").Normalize(
            new RawRecord("mbta-vp", receivedAt, payload, Topic: null), drops ?? new DropLog());

    private static Rt.FeedEntity Entity(Rt.VehiclePosition vehicle) => new() { Id = vehicle.Vehicle?.Id ?? "x", Vehicle = vehicle };

    private static VehiclePosition Position(Rt.VehiclePosition vehicle) => Assert.Single(Normalize(_receivedAt, Entity(vehicle)));

    /// <summary>
    /// The vehicle is skipped and counted as dropped for <paramref name="reason"/> (not at all when <see langword="null"/>:
    /// the entity is not a vehicle record), and a valid one in the same snapshot is still read.
    /// </summary>
    private static void AssertSkipped(Rt.FeedEntity entity, string? reason)
    {
        var other = Sample(v => v.Vehicle.Id = "y0001");
        var drops = new DropLog();

        Assert.Equal("y0001", Assert.Single(Normalize(drops, _receivedAt, entity, Entity(other))).VehicleId);
        string[] expected = reason is null ? [] : [reason];
        Assert.Equal(expected, drops.Reasons);
    }

    [Fact]
    public void Real_record_maps_every_field()
    {
        Assert.Equal(new VehiclePosition(
            "mbta", "y1256", "78210003", "28", 42.2943192f, -71.0880432f, null, 0f, null,
            OccupancyLevel.ManySeatsAvailable, _observedAt), Position(Sample()));
    }

    [Fact]
    public void Reads_the_feed_ids_it_was_given()
    {
        var normalizer = new GtfsRtPositionNormalizer("nl", "nl-vp");
        var message = new Rt.FeedMessage { Header = new Rt.FeedHeader { GtfsRealtimeVersion = "2.0" } };
        message.Entity.Add(Entity(Sample()));

        var positions = normalizer.Normalize(new RawRecord("nl-vp", _receivedAt, message.ToByteArray(), Topic: null), new DropLog());

        Assert.Equal(["nl-vp"], normalizer.RawFeedIds);
        Assert.Equal("nl", Assert.Single(positions).FeedId);
    }

    [Fact]
    public void Delay_is_never_set()
    {
        Assert.Null(Position(Sample()).DelaySec);
    }

    [Fact]
    public void Speed_is_kept_when_set()
    {
        Assert.Equal(7.3f, Position(Sample(v => v.Position.Speed = 7.3f)).SpeedMps);
    }

    [Fact]
    public void Unset_speed_and_bearing_stay_null()
    {
        var position = Position(Sample(v => v.Position.ClearBearing()));

        Assert.Null(position.SpeedMps);
        Assert.Null(position.Bearing);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Impossible_speed_becomes_null(float speed)
    {
        Assert.Null(Position(Sample(v => v.Position.Speed = speed)).SpeedMps);
    }

    [Theory]
    [InlineData(360f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Bearing_outside_0_to_360_becomes_null(float bearing)
    {
        Assert.Null(Position(Sample(v => v.Position.Bearing = bearing)).Bearing);
    }

    [Theory]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.Empty, OccupancyLevel.Empty)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.ManySeatsAvailable, OccupancyLevel.ManySeatsAvailable)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.FewSeatsAvailable, OccupancyLevel.FewSeatsAvailable)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.StandingRoomOnly, OccupancyLevel.StandingRoomOnly)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.CrushedStandingRoomOnly, OccupancyLevel.CrushedStandingRoomOnly)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.Full, OccupancyLevel.Full)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.NotAcceptingPassengers, OccupancyLevel.NotAcceptingPassengers)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.NoDataAvailable, null)]
    [InlineData((int)Rt.VehiclePosition.Types.OccupancyStatus.NotBoardable, null)]
    [InlineData(42, null)]
    public void Occupancy_maps_to_the_model(int status, OccupancyLevel? expected)
    {
        // An int, not the enum: the generated types are internal and cannot appear in a public test signature.
        Assert.Equal(expected, Position(Sample(v => v.OccupancyStatus = (Rt.VehiclePosition.Types.OccupancyStatus)status)).Occupancy);
    }

    [Fact]
    public void Unset_occupancy_is_null()
    {
        // Unset reads as EMPTY (the enum's zero), which would claim an empty vehicle.
        Assert.Null(Position(Sample(v => v.ClearOccupancyStatus())).Occupancy);
    }

    [Fact]
    public void Blank_or_missing_trip_ids_become_null()
    {
        var blank = Position(Sample(v => { v.Trip.TripId = " "; v.Trip.RouteId = ""; }));
        var missing = Position(Sample(v => v.Trip = null));

        Assert.Equal((null, null), (blank.TripId, blank.RouteId));
        Assert.Equal((null, null), (missing.TripId, missing.RouteId));
    }

    [Fact]
    public void Record_exactly_at_the_age_limit_is_kept()
    {
        Assert.Single(Normalize(_observedAt + GtfsRtPositionNormalizer.MaxRecordAge, Entity(Sample())));
    }

    [Fact]
    public void Record_older_than_the_age_limit_is_skipped()
    {
        var receivedAt = _observedAt + GtfsRtPositionNormalizer.MaxRecordAge + TimeSpan.FromSeconds(1);
        var fresh = Sample(v => { v.Vehicle.Id = "y0001"; v.Timestamp = (ulong)receivedAt.ToUnixTimeSeconds(); });
        var drops = new DropLog();

        Assert.Equal("y0001", Assert.Single(Normalize(drops, receivedAt, Entity(Sample()), Entity(fresh))).VehicleId);
        Assert.Equal([PositionMetrics.Stale], drops.Reasons);
    }

    private static readonly Dictionary<string, (Action<Rt.VehiclePosition> Change, string Reason)> _unusable = new()
    {
        ["no vehicle descriptor"] = (v => v.Vehicle = null, PositionMetrics.Incomplete),
        ["blank vehicle id"] = (v => v.Vehicle.Id = " ", PositionMetrics.Incomplete),
        ["no position"] = (v => v.Position = null, PositionMetrics.BadLocation),
        ["no timestamp"] = (v => v.ClearTimestamp(), PositionMetrics.Incomplete),
        ["zero timestamp"] = (v => v.Timestamp = 0, PositionMetrics.Incomplete),
        // Past 9999-12-31 but positive as a long: the conversion would throw. (ulong.MaxValue becomes -1, i.e. 1969.)
        ["timestamp past year 9999"] = (v => v.Timestamp = 253_402_300_800, PositionMetrics.Incomplete),
        ["timestamp long.MaxValue"] = (v => v.Timestamp = long.MaxValue, PositionMetrics.Incomplete),
        ["latitude NaN"] = (v => v.Position.Latitude = float.NaN, PositionMetrics.BadLocation),
        ["latitude 91"] = (v => v.Position.Latitude = 91, PositionMetrics.BadLocation),
        ["longitude -181"] = (v => v.Position.Longitude = -181, PositionMetrics.BadLocation),
    };

    public static TheoryData<string> UnusableVehicles => [.. _unusable.Keys];

    [Theory]
    [MemberData(nameof(UnusableVehicles))]
    public void Unusable_vehicle_is_skipped(string vehicle)
    {
        var (change, reason) = _unusable[vehicle];

        AssertSkipped(Entity(Sample(change)), reason);
    }

    [Fact]
    public void Deleted_entity_is_skipped()
    {
        var entity = Entity(Sample());
        entity.IsDeleted = true;

        AssertSkipped(entity, reason: null);
    }

    [Fact]
    public void Entity_without_a_vehicle_is_skipped()
    {
        AssertSkipped(
            new Rt.FeedEntity { Id = "t1", TripUpdate = new Rt.TripUpdate { Trip = new Rt.TripDescriptor { TripId = "1" } } },
            reason: null);
    }

    [Fact]
    public void Snapshot_without_entities_yields_nothing()
    {
        Assert.Empty(Normalize(_receivedAt));
    }

    [Fact]
    public void Truncated_payload_is_a_format_error()
    {
        // Field 1 (header) announces 5 bytes but only 1 follows.
        Assert.Throws<FormatException>(() => Normalize([0x0A, 0x05, 0x01], _receivedAt));
    }
}

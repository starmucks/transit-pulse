using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;

namespace TransitPulse.Ingestor.Tests.Normalization;

public sealed class PositionDeduplicatorTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly PositionDeduplicator _deduplicator = new();

    private static VehiclePosition At(int seconds, string vehicleId = "40/415", string feedId = "hsl", double lat = 60.2) =>
        AtTime(_t0.AddSeconds(seconds), vehicleId, feedId, lat);

    private static VehiclePosition AtTime(DateTimeOffset observedAt, string vehicleId = "40/415", string feedId = "hsl", double lat = 60.2) =>
        new(feedId, vehicleId, null, null, lat, 24.9, null, null, null, null, observedAt);

    [Fact]
    public void First_position_of_a_vehicle_is_accepted()
    {
        Assert.True(_deduplicator.TryAccept(At(0)));
    }

    [Fact]
    public void Newer_position_of_the_same_vehicle_is_accepted()
    {
        _deduplicator.TryAccept(At(0));

        Assert.True(_deduplicator.TryAccept(At(1)));
    }

    [Fact]
    public void Repeat_with_the_same_time_is_dropped()
    {
        _deduplicator.TryAccept(At(0));

        // A polled snapshot repeats a vehicle that has not reported since: other fields may differ, the time does not.
        Assert.False(_deduplicator.TryAccept(At(0, lat: 60.3)));
    }

    [Fact]
    public void Late_position_is_dropped_and_does_not_move_the_vehicle_back()
    {
        _deduplicator.TryAccept(At(10));

        Assert.False(_deduplicator.TryAccept(At(5)));
        // Still compared with 10 s, not with the dropped 5 s.
        Assert.False(_deduplicator.TryAccept(At(7)));
        Assert.True(_deduplicator.TryAccept(At(11)));
    }

    [Fact]
    public void Vehicles_are_tracked_separately()
    {
        _deduplicator.TryAccept(At(10, vehicleId: "40/415"));

        Assert.True(_deduplicator.TryAccept(At(0, vehicleId: "40/416")));
        Assert.Equal(2, _deduplicator.VehicleCount);
    }

    [Fact]
    public void Same_vehicle_id_in_another_feed_is_another_vehicle()
    {
        _deduplicator.TryAccept(At(10, vehicleId: "1234", feedId: "mbta"));

        Assert.True(_deduplicator.TryAccept(At(0, vehicleId: "1234", feedId: "entur-sky")));
    }

    [Fact]
    public void Time_is_compared_as_an_instant()
    {
        _deduplicator.TryAccept(At(0));

        // Sources report sub-second times (HSL: milliseconds): any step forward counts as newer.
        Assert.True(_deduplicator.TryAccept(AtTime(_t0.AddTicks(1))));
    }
}

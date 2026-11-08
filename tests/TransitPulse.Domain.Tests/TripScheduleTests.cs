namespace TransitPulse.Domain.Tests;

public sealed class TripScheduleTests
{
    private static readonly DateOnly _day = new(2026, 11, 4);
    private static readonly DateOnly _nextDay = _day.AddDays(1);

    private static ScheduledTrip Trip(string tripId, string routeId, int direction, int hours, int minutes, DateOnly? date = null) =>
        new(tripId, routeId, direction, (hours * 3600) + (minutes * 60), date ?? _day);

    private static TripSchedule Schedule(params ScheduledTrip[] trips) => new([_day, _nextDay], trips);

    [Fact]
    public void Route_id_finds_the_run()
    {
        var run = Trip("1001_20261103_Ke_1_0537", "1001", 0, 5, 37);

        var result = Schedule(run).Find("1001/0/20261104/05:37:00", out var trip);

        Assert.Equal(TripResolution.Found, result);
        Assert.Same(run, trip);
    }

    [Fact]
    public void Route_variant_finds_the_run_and_gives_the_real_route()
    {
        var schedule = Schedule(Trip("100HI9_20261103_Ke_1_1924", "100H", 0, 19, 24));

        var result = schedule.Find("100HI9/0/20261104/19:24:00", out var trip);

        Assert.Equal(TripResolution.Found, result);
        Assert.Equal("100H", trip?.RouteId);
    }

    [Fact]
    public void Time_past_midnight_finds_the_run_of_the_earlier_service_day()
    {
        var run = Trip("1007H6_20261103_Ke_1_2614", "1007", 0, 26, 14);

        var result = Schedule(run).Find("1007H6/0/20261104/26:14:00", out var trip);

        Assert.Equal(TripResolution.Found, result);
        Assert.Same(run, trip);
    }

    [Fact]
    public void Same_trip_on_two_days_is_told_apart_by_the_day()
    {
        var first = Trip("1001_x", "1001", 0, 5, 37, _day);
        var second = Trip("1001_x", "1001", 0, 5, 37, _nextDay);

        Schedule(first, second).Find("1001/0/20261105/05:37:00", out var trip);

        Assert.Same(second, trip);
    }

    [Fact]
    public void Two_runs_of_a_route_at_one_time_are_ambiguous_by_route_but_not_by_variant()
    {
        var plain = Trip("1006_20261103_Ke_2_0728", "1006", 1, 7, 28);
        var variant = Trip("1006 6_20261103_Ke_2_0728", "1006", 1, 7, 28);
        var schedule = Schedule(plain, variant);

        var byRoute = schedule.Find("1006/1/20261104/07:28:00", out var none);
        var byVariant = schedule.Find("1006 6/1/20261104/07:28:00", out var trip);

        Assert.Equal(TripResolution.Ambiguous, byRoute);
        Assert.Null(none);
        Assert.Equal(TripResolution.Found, byVariant);
        Assert.Same(variant, trip);
    }

    [Fact]
    public void Three_runs_under_one_key_stay_ambiguous()
    {
        var schedule = Schedule(
            Trip("100HA3_x", "100H", 1, 5, 11), Trip("100HD3_x", "100H", 1, 5, 11), Trip("100HM4_x", "100H", 1, 5, 11));

        Assert.Equal(TripResolution.Ambiguous, schedule.Find("100H/1/20261104/05:11:00", out _));
    }

    [Fact]
    public void Trip_id_without_a_variant_is_found_by_route()
    {
        var schedule = Schedule(Trip("plain", "1001", 0, 5, 37));

        Assert.Equal(TripResolution.Found, schedule.Find("1001/0/20261104/05:37:00", out _));
        Assert.Equal(TripResolution.RouteUnknown, schedule.Find("plain/0/20261104/05:37:00", out _));
    }

    [Theory]
    [InlineData("1001/1/20261104/05:37:00")] // other direction
    [InlineData("1001/0/20261104/05:38:00")] // other time
    [InlineData("1001/0/20261105/05:37:00")] // a loaded day without that run
    public void Known_route_without_that_departure_is_no_departure(string descriptor)
    {
        var schedule = Schedule(Trip("1001_x", "1001", 0, 5, 37));

        Assert.Equal(TripResolution.NoDeparture, schedule.Find(descriptor, out var trip));
        Assert.Null(trip);
    }

    [Fact]
    public void Route_nobody_runs_is_unknown()
    {
        var schedule = Schedule(Trip("1001_x", "1001", 0, 5, 37));

        Assert.Equal(TripResolution.RouteUnknown, schedule.Find("1009TX/0/20261104/08:16:00", out _));
    }

    [Fact]
    public void Day_outside_the_schedule_is_not_loaded_even_for_an_unknown_route()
    {
        var schedule = Schedule(Trip("1001_x", "1001", 0, 5, 37));

        Assert.Equal(TripResolution.DayNotLoaded, schedule.Find("1001/0/20261103/05:37:00", out _));
        Assert.Equal(TripResolution.DayNotLoaded, schedule.Find("1009TX/0/20261103/05:37:00", out _));
    }

    [Fact]
    public void Position_without_a_trip_has_no_descriptor()
    {
        Assert.Equal(TripResolution.NoDescriptor, Schedule().Find(null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1001/0/20261104")]
    [InlineData("1001/0/20261104/05:37:00/extra")]
    [InlineData("1001/2/20261104/05:37:00")]
    [InlineData("1001/x/20261104/05:37:00")]
    [InlineData("1001/0/2026-11-04/05:37:00")]
    [InlineData("1001/0/20261304/05:37:00")]
    [InlineData("1001/0/20261104/05:37")]
    [InlineData("1001/0/20261104/5:37:00")]
    [InlineData("1001/0/20261104/05-37-00")]
    [InlineData("1001/0/20261104/-5:37:00")]
    [InlineData("/0/20261104/05:37:00")]
    [InlineData("1001/0/20261104/05:60:00")]
    [InlineData("1001/0/20261104/05:37:60")]
    public void Anything_else_is_malformed(string descriptor)
    {
        Assert.Equal(TripResolution.Malformed, Schedule().Find(descriptor, out _));
    }

    [Theory]
    [InlineData("100HI9/0/20261104/19:24:00")] // found by variant
    [InlineData("1009TX/0/20261104/08:16:00")] // unknown route
    [InlineData("1001/0/20261104/05:37")] // malformed
    public void Lookup_allocates_nothing(string descriptor)
    {
        var schedule = Schedule(Trip("100HI9_20261103_Ke_1_1924", "100H", 0, 19, 24));
        schedule.Find(descriptor, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        schedule.Find(descriptor, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Run_outside_the_given_days_is_rejected()
    {
        var late = Trip("1001_x", "1001", 0, 5, 37, _nextDay.AddDays(1));

        Assert.Throws<ArgumentException>(() => Schedule(late));
    }
}

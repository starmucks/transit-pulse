namespace TransitPulse.Domain.Tests;

public sealed class ScheduleDeviationTests
{
    private static readonly TimeZoneInfo _helsinki = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    [Fact]
    public void Trip_after_midnight_stays_on_its_service_day()
    {
        DateOnly serviceDate = new(2026, 11, 4);
        var actual = new DateTimeOffset(2026, 11, 4, 23, 40, 0, TimeSpan.Zero); // 01:40 in Helsinki the next day.

        var delay = ScheduleDeviation.InSeconds(serviceDate, (25 * 3600) + (35 * 60), actual, _helsinki);

        Assert.Equal(5 * 60, delay);
    }

    [Fact]
    public void Early_arrival_is_negative()
    {
        DateOnly serviceDate = new(2026, 11, 4);
        var actual = new DateTimeOffset(2026, 11, 4, 12, 27, 30, TimeSpan.Zero); // 14:27:30 in Helsinki.

        var delay = ScheduleDeviation.InSeconds(serviceDate, (14 * 3600) + (28 * 60), actual, _helsinki);

        Assert.Equal(-30, delay);
    }

    [Fact]
    public void Fractional_seconds_are_rounded_away_from_zero()
    {
        DateOnly serviceDate = new(2026, 11, 4);
        var scheduled = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(1, ScheduleDeviation.InSeconds(serviceDate, 14 * 3600, scheduled.AddMilliseconds(500), _helsinki));
        Assert.Equal(-1, ScheduleDeviation.InSeconds(serviceDate, 14 * 3600, scheduled.AddMilliseconds(-500), _helsinki));
    }

    [Fact]
    public void Daylight_saving_transition_uses_gtfs_noon_minus_twelve_hours()
    {
        DateOnly serviceDate = new(2026, 3, 29);
        // Helsinki's missing wall-clock hour makes 03:30 invalid, but GTFS Time is elapsed from noon minus 12 h.
        var actual = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);

        var delay = ScheduleDeviation.InSeconds(serviceDate, (3 * 3600) + (30 * 60), actual, _helsinki);

        Assert.Equal(0, delay);
    }

    [Fact]
    public void Negative_schedule_time_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScheduleDeviation.InSeconds(new DateOnly(2026, 11, 4), -1, DateTimeOffset.UtcNow, _helsinki));
    }
}

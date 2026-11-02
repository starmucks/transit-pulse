using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Infrastructure.Tests.Gtfs;

public sealed class GtfsFeedTests
{
    [Fact]
    public void Reads_every_file_of_a_real_snapshot_slice()
    {
        using var feed = GtfsFixture.Feed(GtfsFixture.Tram9Files());

        Assert.Equal(new GtfsRoute("1009", "9", "Länsisatama - Päärautatieas. - Kallio - Pasila as. - Ilmala", 0), Assert.Single(feed.Routes()));
        Assert.Equal(54, feed.Stops().Count());
        Assert.Equal(3, feed.Trips().Count());
        Assert.Equal(81, feed.StopTimes().Count());
        Assert.Equal(678, feed.ShapePoints().Count());
        Assert.Equal(
            new GtfsCalendar("1009_20261030_20261101_Ke", GtfsWeekdays.Wednesday, new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 1)),
            Assert.Single(feed.Calendars()));
        Assert.Empty(feed.CalendarDates());
    }

    [Fact]
    public void Keeps_times_past_midnight_and_reads_a_blank_parent_station_as_none()
    {
        using var feed = GtfsFixture.Feed(GtfsFixture.Tram9Files());

        var nightStart = feed.StopTimes().First(time => time.TripId == "1009_20261030_Ke_2_2535" && time.StopSequence == 1);
        Assert.Equal(new TimeSpan(1, 1, 35, 0), nightStart.Departure);
        Assert.Equal(0.0, nightStart.ShapeDistTraveled);
        // HSL writes an empty parent_station as a single space.
        Assert.All(feed.Stops(), stop => Assert.Null(stop.ParentStation));
    }

    [Fact]
    public void Missing_file_is_reported_when_read()
    {
        var files = GtfsFixture.Tram9Files();
        files.Remove("routes.txt");
        using var feed = GtfsFixture.Feed(files);

        Assert.False(feed.Contains("routes.txt"));
        var error = Assert.Throws<GtfsFormatException>(() => feed.Routes().ToList());
        Assert.Equal("routes.txt: the file is missing from the snapshot", error.Message);
    }

    [Fact]
    public void Calendar_dates_tell_added_from_removed_days()
    {
        using var feed = GtfsFixture.Feed(new() { ["calendar_dates.txt"] = "service_id,date,exception_type\nS,20261102,1\nS,20261103,2\n" });

        Assert.Equal(
            [new GtfsCalendarDate("S", new DateOnly(2026, 11, 2), true), new GtfsCalendarDate("S", new DateOnly(2026, 11, 3), false)],
            feed.CalendarDates());
    }

    [Theory]
    [InlineData("calendar_dates.txt", "service_id,date,exception_type\nS,20261102,3\n", "exception_type must be 1 (added) or 2 (removed)")]
    [InlineData("calendar.txt", "service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date\nS,1,1,1,1,2,0,0,20261102,20261231\n", "friday must be 0 or 1")]
    public void Calendar_values_outside_their_codes_are_rejected(string file, string csv, string expected)
    {
        using var feed = GtfsFixture.Feed(new() { [file] = csv });

        var error = Assert.Throws<GtfsFormatException>(() => file == "calendar.txt" ? feed.Calendars().ToList<object>() : feed.CalendarDates().ToList<object>());
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}

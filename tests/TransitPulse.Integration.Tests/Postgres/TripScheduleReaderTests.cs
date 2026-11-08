using Npgsql;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class TripScheduleReaderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string Service = "1009_20261030_20261101_Ke";
    private const string CalendarHeader = "service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date\n";
    private const string DatesHeader = "service_id,date,exception_type\n";

    // The slice's three runs of tram 9, under a calendar of Wednesdays from 28 October to 4 November.
    private const string Wednesdays = $"{CalendarHeader}{Service},0,0,1,0,0,0,0,20261028,20261104\n";
    private const int RunsPerDay = 3;

    private static readonly DateOnly _wednesday = new(2026, 11, 4);
    private static readonly DateOnly _previousWednesday = new(2026, 10, 28);
    private static readonly DateOnly _thursday = new(2026, 11, 5);

    private readonly string _directory = Directory.CreateTempSubdirectory("gtfs-schedule-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_past_midnight_is_found_in_its_service_day()
    {
        var reader = new TripScheduleReader(await ImportAsync(Wednesdays));

        var loaded = await reader.ReadAsync("hsl", [_wednesday], Ct);

        Assert.Equal(RunsPerDay, loaded!.Schedule.RunCount);
        Assert.Equal(TripResolution.Found, loaded.Schedule.Find("1009/1/20261104/25:35:00", out var trip));
        Assert.Equal(new ScheduledTrip("1009_20261030_Ke_2_2535", "1009", 1, (25 * 3600) + (35 * 60), _wednesday), trip! with { Shape = null });
    }

    [Fact]
    public async Task Runs_carry_their_shape_one_instance_per_shape()
    {
        var reader = new TripScheduleReader(await ImportAsync(Wednesdays));

        var loaded = await reader.ReadAsync("hsl", [_wednesday], Ct);
        loaded!.Schedule.Find("1009/0/20261104/14:28:00", out var outward);
        loaded.Schedule.Find("1009/1/20261104/14:52:00", out var back);
        loaded.Schedule.Find("1009/1/20261104/25:35:00", out var lateBack);

        Assert.NotNull(outward!.Shape);
        Assert.NotSame(outward.Shape, back!.Shape);
        Assert.Same(back.Shape, lateBack!.Shape);

        // HSL's own shape_dist_traveled, in kilometres: 9.900 at the last point of direction 1.
        Assert.Equal(9_900, back.Shape!.LengthMetres, tolerance: 99);
    }

    [Fact]
    public async Task First_departure_is_taken_from_the_first_stop()
    {
        var reader = new TripScheduleReader(await ImportAsync(Wednesdays));

        var loaded = await reader.ReadAsync("hsl", [_wednesday], Ct);

        Assert.Equal(TripResolution.Found, loaded!.Schedule.Find("1009/0/20261104/14:28:00", out _));
        Assert.Equal(TripResolution.NoDeparture, loaded.Schedule.Find("1009/0/20261104/14:30:00", out _));
    }

    [Fact]
    public async Task Several_days_hold_a_run_each()
    {
        var reader = new TripScheduleReader(await ImportAsync(Wednesdays));

        var loaded = await reader.ReadAsync("hsl", [_previousWednesday, _wednesday, _thursday], Ct);

        Assert.Equal(2 * RunsPerDay, loaded!.Schedule.RunCount);
        Assert.Equal(TripResolution.Found, loaded.Schedule.Find("1009/0/20261028/14:28:00", out var trip));
        Assert.Equal(_previousWednesday, trip!.ServiceDate);
        Assert.Equal(TripResolution.NoDeparture, loaded.Schedule.Find("1009/0/20261105/14:28:00", out _));
    }

    [Theory]
    [InlineData("1,0,0,0,0,0,0", "2026-11-02", RunsPerDay)] // Monday is the mask's lowest bit
    [InlineData("0,0,0,0,0,0,1", "2026-11-01", RunsPerDay)] // Sunday is the highest
    [InlineData("1,1,1,1,1,1,0", "2026-11-01", 0)]
    [InlineData("1,0,0,0,0,0,0", "2026-10-26", 0)] // a Monday before the start date
    [InlineData("0,0,1,0,0,0,0", "2026-11-11", 0)] // a Wednesday after the end date
    public async Task Calendar_decides_by_weekday_and_date_range(string weekdays, string date, int runs)
    {
        var reader = new TripScheduleReader(await ImportAsync($"{CalendarHeader}{Service},{weekdays},20261028,20261104\n"));

        var loaded = await reader.ReadAsync("hsl", [DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)], Ct);

        Assert.Equal(runs, loaded!.Schedule.RunCount);
    }

    [Theory]
    [InlineData("20261104,2", "2026-11-04", 0)] // removed from a day it runs on
    [InlineData("20261105,1", "2026-11-05", RunsPerDay)] // added to a day it does not run on
    [InlineData("20261104,1", "2026-11-04", RunsPerDay)] // added to a day it already runs on: not twice
    [InlineData("20261104,2", "2026-10-28", RunsPerDay)] // an exception only touches its own day
    public async Task Calendar_dates_override_the_calendar(string exception, string date, int runs)
    {
        var reader = new TripScheduleReader(await ImportAsync(Wednesdays, $"{DatesHeader}{Service},{exception}\n"));

        var loaded = await reader.ReadAsync("hsl", [DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)], Ct);

        Assert.Equal(runs, loaded!.Schedule.RunCount);
    }

    [Fact]
    public async Task Feed_without_a_snapshot_has_no_schedule()
    {
        var reader = new TripScheduleReader(await ImportAsync(Wednesdays, feedId: "mbta"));

        Assert.Null(await reader.ActiveSnapshotAsync("hsl", Ct));
        Assert.Null(await reader.ReadAsync("hsl", [_wednesday], Ct));
    }

    [Fact]
    public async Task New_snapshot_replaces_the_old_one()
    {
        var database = await ImportAsync(Wednesdays);
        var reader = new TripScheduleReader(database);
        var first = await reader.ActiveSnapshotAsync("hsl", Ct);

        var second = await new GtfsImporter(database).ImportAsync(
            "hsl", GtfsZips.Tram9(Path.Combine(_directory, "new.zip"), files => files["calendar.txt"] = $"{CalendarHeader}{Service},0,0,0,1,0,0,0,20261028,20261105\n"), Ct);

        var loaded = await reader.ReadAsync("hsl", [_wednesday, _thursday], Ct);
        Assert.NotEqual(first, second.SnapshotId);
        Assert.Equal(second.SnapshotId, await reader.ActiveSnapshotAsync("hsl", Ct));
        Assert.Equal(second.SnapshotId, loaded!.SnapshotId);
        Assert.Equal(TripResolution.NoDeparture, loaded.Schedule.Find("1009/0/20261104/14:28:00", out _));
        Assert.Equal(TripResolution.Found, loaded.Schedule.Find("1009/0/20261105/14:28:00", out _));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<NpgsqlDataSource> ImportAsync(string calendar, string? calendarDates = null, string feedId = "hsl")
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var zip = GtfsZips.Tram9(Path.Combine(_directory, "snapshot.zip"), files =>
        {
            files["calendar.txt"] = calendar;
            if (calendarDates is not null)
            {
                files["calendar_dates.txt"] = calendarDates;
            }
        });

        var result = await new GtfsImporter(database).ImportAsync(feedId, zip, Ct);
        Assert.True(result.Report!.IsValid, string.Join("; ", result.Report.Problems));
        return database;
    }
}

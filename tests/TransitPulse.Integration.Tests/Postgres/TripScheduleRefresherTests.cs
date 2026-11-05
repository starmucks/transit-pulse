using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Processor;
using TransitPulse.Processor.Schedule;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class TripScheduleRefresherTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    // The slice's trams run on Wednesdays only; 2026-11-04 is one, and nothing else is in the date range.
    private const string Service = "1009_20261030_20261101_Ke";
    private const string CalendarHeader = "service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date\n";
    private const string Wednesday = $"{CalendarHeader}{Service},0,0,1,0,0,0,0,20261104,20261104\n";
    private const string TramOnWednesday = "1009/0/20261104/14:28:00";

    private readonly string _directory = Directory.CreateTempSubdirectory("gtfs-refresh-").FullName;
    private readonly CurrentTripSchedule _schedule = new("hsl");
    private readonly FakeLogger<TripScheduleRefresher> _log = new();
    private FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TripResolution? Lookup() =>
        _schedule.Resolve(new VehiclePosition("hsl", "40/415", TramOnWednesday, "1009", 60.2, 24.9, null, null, null, null, _clock.GetUtcNow()));

    private int Loads => _log.Collector.GetSnapshot().Count(record => record.Message.StartsWith("Schedule of hsl:", StringComparison.Ordinal));

    [Fact]
    public async Task Start_loads_the_schedule_before_returning()
    {
        using var refresher = Refresher(await ImportedDatabaseAsync());

        await refresher.StartAsync(Ct);

        Assert.Equal(TripResolution.Found, Lookup());
        await refresher.StopAsync(Ct);
    }

    [Theory]
    [InlineData("2026-11-03T12:00:00Z", TripResolution.Found)] // tomorrow
    [InlineData("2026-11-05T12:00:00Z", TripResolution.Found)] // yesterday
    [InlineData("2026-11-01T12:00:00Z", TripResolution.DayNotLoaded)]
    [InlineData("2026-11-02T21:59:00Z", TripResolution.DayNotLoaded)] // 23:59 in Helsinki: its tomorrow is the 3rd
    [InlineData("2026-11-02T22:00:00Z", TripResolution.Found)] // 00:00 in Helsinki, but still the 2nd in UTC
    public async Task Days_held_are_today_and_one_either_side_in_the_feeds_time_zone(string now, TripResolution expected)
    {
        _clock = new FakeTimeProvider(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        using var refresher = Refresher(await ImportedDatabaseAsync());

        await refresher.RefreshAsync(Ct);

        Assert.Equal(expected, Lookup());
    }

    [Fact]
    public async Task Same_day_and_snapshot_are_not_read_again()
    {
        using var refresher = Refresher(await ImportedDatabaseAsync());

        await refresher.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await refresher.RefreshAsync(Ct);

        Assert.Equal(1, Loads);
    }

    [Fact]
    public async Task New_day_is_read_without_a_new_snapshot()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        using var refresher = Refresher(await ImportedDatabaseAsync());
        await refresher.RefreshAsync(Ct);
        Assert.Equal(TripResolution.DayNotLoaded, Lookup());

        _clock.Advance(TimeSpan.FromDays(2));
        await refresher.RefreshAsync(Ct);

        Assert.Equal(TripResolution.Found, Lookup());
        Assert.Equal(2, Loads);
    }

    [Fact]
    public async Task New_snapshot_is_read_on_the_same_day()
    {
        var database = await ImportedDatabaseAsync();
        using var refresher = Refresher(database);
        await refresher.RefreshAsync(Ct);

        // The Tuesday before: the tram no longer runs on Wednesday.
        var zip = GtfsZips.Tram9(Path.Combine(_directory, "new.zip"), files => files["calendar.txt"] = $"{CalendarHeader}{Service},0,1,0,0,0,0,0,20261103,20261103\n");
        await new GtfsImporter(database).ImportAsync("hsl", zip, Ct);
        await refresher.RefreshAsync(Ct);

        Assert.Equal(TripResolution.NoDeparture, Lookup());
        Assert.Equal(2, Loads);
    }

    [Fact]
    public async Task Fixed_service_date_replaces_today()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero));
        using var refresher = Refresher(await ImportedDatabaseAsync(), new TripScheduleOptions { ServiceDate = new DateOnly(2026, 11, 4) });

        await refresher.RefreshAsync(Ct);

        Assert.Equal(TripResolution.Found, Lookup());
    }

    [Fact]
    public async Task Without_a_snapshot_positions_go_on_and_a_later_import_is_picked_up()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        using var refresher = Refresher(database);

        await refresher.RefreshAsync(Ct);
        Assert.Equal(TripResolution.NoSchedule, Lookup());

        await ImportAsync(database);
        await refresher.RefreshAsync(Ct);
        Assert.Equal(TripResolution.Found, Lookup());
    }

    [Fact]
    public async Task Database_without_the_schema_is_a_logged_failure_not_an_exception()
    {
        using var refresher = Refresher(await postgres.CreateDatabaseAsync());

        await refresher.RefreshAsync(Ct);

        Assert.Equal(TripResolution.NoSchedule, Lookup());
        Assert.Contains(_log.Collector.GetSnapshot(), record => record.Message.StartsWith("Could not read the schedule", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_read_keeps_the_old_schedule()
    {
        var database = await ImportedDatabaseAsync();
        using var refresher = Refresher(database);
        await refresher.RefreshAsync(Ct);

        await using (var hide = database.CreateCommand("ALTER TABLE gtfs.snapshot RENAME TO snapshot_hidden"))
        {
            await hide.ExecuteNonQueryAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        await refresher.RefreshAsync(Ct);

        Assert.Equal(TripResolution.Found, Lookup());
        Assert.Contains(_log.Collector.GetSnapshot(), record => record.Message.StartsWith("Could not read the schedule", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_refresh_ends_with_an_exception_instead_of_a_failure_log()
    {
        using var refresher = Refresher(await ImportedDatabaseAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresher.RefreshAsync(new CancellationToken(canceled: true)));

        Assert.DoesNotContain(_log.Collector.GetSnapshot(), record => record.Message.StartsWith("Could not read", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private TripScheduleRefresher Refresher(NpgsqlDataSource database, TripScheduleOptions? options = null) =>
        new(new TripScheduleReader(database), _schedule, Options.Create(options ?? new TripScheduleOptions()), _clock, _log);

    private async Task<NpgsqlDataSource> ImportedDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        await ImportAsync(database);
        return database;
    }

    private async Task ImportAsync(NpgsqlDataSource database)
    {
        var zip = GtfsZips.Tram9(Path.Combine(_directory, "snapshot.zip"), files => files["calendar.txt"] = Wednesday);
        await new GtfsImporter(database).ImportAsync("hsl", zip, Ct);
    }
}

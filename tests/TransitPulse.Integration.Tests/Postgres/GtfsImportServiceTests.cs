using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Npgsql;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Worker.Gtfs;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class GtfsImportServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gtfs-service-").FullName;
    private readonly FakeLogger<GtfsImportService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rejected_snapshot_is_logged_once_and_other_feeds_still_import()
    {
        var service = await ServiceAsync();
        Snapshot("hsl", edit: files => files["routes.txt"] = "route_id,route_type\n");
        Snapshot("mbta");

        await service.ImportAllAsync(Ct);
        await service.ImportAllAsync(Ct);

        var logs = _logger.Collector.GetSnapshot();
        Assert.Single(logs, log => log.Level == LogLevel.Warning && log.Message.Contains("GTFS hsl", StringComparison.Ordinal));
        Assert.Single(logs, log => log.Message.StartsWith("GTFS mbta", StringComparison.Ordinal) && log.Message.Contains("imported", StringComparison.Ordinal));
        Assert.Single(logs, log => log.Message.StartsWith("GTFS mbta", StringComparison.Ordinal) && log.Message.Contains("already active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_newer_file_after_a_rejected_one_is_imported()
    {
        var service = await ServiceAsync();
        Snapshot("hsl", "2026-11-01T1433Z", files => files["routes.txt"] = "route_id,route_type\n");
        await service.ImportAllAsync(Ct);

        Snapshot("hsl", "2026-11-02T1433Z");
        await service.ImportAllAsync(Ct);

        Assert.Single(_logger.Collector.GetSnapshot(), log => log.Message.Contains("2026-11-02T1433Z.zip imported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_zip_of_one_feed_is_logged_and_does_not_stop_the_others()
    {
        var service = await ServiceAsync();
        var broken = Snapshot("hsl");
        File.WriteAllText(broken, "not a zip");
        Snapshot("mbta");

        await service.ImportAllAsync(Ct);

        var logs = _logger.Collector.GetSnapshot();
        var failed = Assert.Single(logs, log => log.Level == LogLevel.Error);
        Assert.StartsWith("GTFS hsl", failed.Message, StringComparison.Ordinal);
        Assert.Single(logs, log => log.Message.StartsWith("GTFS mbta", StringComparison.Ordinal) && log.Message.Contains("imported", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<GtfsImportService> ServiceAsync()
    {
        NpgsqlDataSource database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var options = Options.Create(new GtfsImportOptions { SnapshotDirectory = _root });
        return new GtfsImportService(database, new GtfsImporter(database), options, TimeProvider.System, _logger);
    }

    // A complete snapshot as the collector writes it: the zip and its non-empty .zip.sha256.
    private string Snapshot(string feed, string stamp = "2026-11-01T1433Z", Action<Dictionary<string, string>>? edit = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, feed)).FullName;
        var zip = GtfsZips.Tram9(Path.Combine(folder, stamp + ".zip"), edit);
        File.WriteAllText(zip + ".sha256", "0123  " + stamp + ".zip");
        return zip;
    }
}

using System.IO.Compression;
using Npgsql;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class GtfsImporterTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    // Table partitions only: pg_inherits also links each partition's primary key index to the parent's (relkind 'I').
    private const string PartitionCount =
        "SELECT count(*) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhparent JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE n.nspname = 'gtfs' AND c.relkind = 'p'";

    private static readonly string _tram9 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gtfs", "hsl-tram-9");

    private readonly string _directory = Directory.CreateTempSubdirectory("gtfs-import-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fixture_snapshot_is_copied_row_for_row_and_becomes_active()
    {
        var database = await MigratedDatabaseAsync();

        var result = await new GtfsImporter(database).ImportAsync("hsl", Zip("hsl.zip"), Ct);

        Assert.Equal(GtfsImportOutcome.Imported, result.Outcome);
        Assert.True(result.Report!.IsValid, string.Join("; ", result.Report.Problems));
        // calendar_dates.txt of the slice has a header only.
        foreach (var table in (string[])["routes", "stops", "calendar", "shapes", "trips", "stop_times"])
        {
            Assert.Equal(result.Report.Rows[$"{table}.txt"], await ScalarAsync<long>(database, $"SELECT count(*) FROM gtfs.{table}"));
        }

        Assert.Equal(result.SnapshotId, await ScalarAsync<int>(database, "SELECT snapshot_id FROM gtfs.snapshot WHERE is_active"));
        Assert.Equal(7, await ScalarAsync<long>(database, PartitionCount));
        // One index per partition: ATTACH adopted the primary key built after the copy instead of building its own.
        Assert.Equal(7, await ScalarAsync<long>(database,
            "SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'gtfs' AND c.relispartition"));
        // 25:35:00 of the service day: past midnight, still the trip's day.
        Assert.Equal(25 * 3600 + 35 * 60, await ScalarAsync<int>(database,
            "SELECT arrival_sec FROM gtfs.stop_times WHERE trip_id = '1009_20261030_Ke_2_2535' AND stop_sequence = 1"));
        // HSL writes parent_station as a single space: stored as NULL, not as " ".
        Assert.Equal(0, await ScalarAsync<long>(database, "SELECT count(*) FROM gtfs.stops WHERE parent_station IS NOT NULL"));
    }

    [Fact]
    public async Task Same_file_again_changes_nothing()
    {
        var database = await MigratedDatabaseAsync();
        var importer = new GtfsImporter(database);
        var zip = Zip("hsl.zip");
        var first = await importer.ImportAsync("hsl", zip, Ct);

        var second = await importer.ImportAsync("hsl", zip, Ct);

        Assert.Equal(GtfsImportOutcome.Unchanged, second.Outcome);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Null(second.Report);
        Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM gtfs.snapshot"));
        Assert.Equal(first.Report!.Rows["stop_times.txt"], await ScalarAsync<long>(database, "SELECT count(*) FROM gtfs.stop_times"));
    }

    [Fact]
    public async Task New_snapshot_replaces_the_active_one_and_the_old_version_is_dropped()
    {
        var database = await MigratedDatabaseAsync();
        var importer = new GtfsImporter(database);
        await importer.ImportAsync("hsl", Zip("old.zip"), Ct);

        var result = await importer.ImportAsync("hsl", Zip("new.zip", RenameRoute), Ct);

        Assert.Equal(GtfsImportOutcome.Imported, result.Outcome);
        Assert.Equal(0, result.StaleSnapshots);
        Assert.Equal(result.SnapshotId, await ScalarAsync<int>(database, "SELECT snapshot_id FROM gtfs.snapshot"));
        Assert.Equal(7, await ScalarAsync<long>(database, PartitionCount));
        Assert.Equal("Renamed", await ScalarAsync<string>(database, "SELECT long_name FROM gtfs.routes"));
    }

    [Fact]
    public async Task Rejected_snapshot_keeps_the_active_one_and_leaves_nothing_behind()
    {
        var database = await MigratedDatabaseAsync();
        var importer = new GtfsImporter(database);
        var active = await importer.ImportAsync("hsl", Zip("good.zip"), Ct);

        var result = await importer.ImportAsync("hsl", Zip("broken.zip", files => files["routes.txt"] = "route_id,route_type\n"), Ct);

        Assert.Equal(GtfsImportOutcome.Rejected, result.Outcome);
        Assert.Contains("routes.txt has no rows", result.Report!.Problems);
        Assert.Equal(active.SnapshotId, await ScalarAsync<int>(database, "SELECT snapshot_id FROM gtfs.snapshot WHERE is_active"));
        Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM gtfs.snapshot"));
        Assert.Equal(7, await ScalarAsync<long>(database, PartitionCount));
    }

    [Fact]
    public async Task Duplicate_stop_sequence_rejects_the_snapshot()
    {
        var database = await MigratedDatabaseAsync();

        var result = await new GtfsImporter(database).ImportAsync("hsl", Zip("duplicate.zip", files =>
        {
            var lines = files["stop_times.txt"].TrimEnd('\n').Split('\n');
            files["stop_times.txt"] = string.Join('\n', [.. lines, lines[1]]) + "\n";
        }), Ct);

        Assert.Equal(GtfsImportOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Report!.Problems, problem => problem.StartsWith("stop_times.txt: duplicate key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reader_does_not_block_the_import_and_delays_only_the_drop_of_the_old_version()
    {
        var database = await MigratedDatabaseAsync();
        var importer = new GtfsImporter(database, cleanupLockTimeout: TimeSpan.FromMilliseconds(200));
        await importer.ImportAsync("hsl", Zip("old.zip"), Ct);
        var newZip = Zip("new.zip", RenameRoute);

        GtfsImportResult blocked;
        await using (var reader = await database.OpenConnectionAsync(Ct))
        await using (var transaction = await reader.BeginTransactionAsync(Ct))
        {
            // A long schedule read in progress, as Processor will do on start.
            await using (var read = new NpgsqlCommand("SELECT count(*) FROM gtfs.routes", reader, transaction))
            {
                await read.ExecuteScalarAsync(Ct);
            }

            blocked = await importer.ImportAsync("hsl", newZip, Ct);
        }

        var retried = await importer.ImportAsync("hsl", newZip, Ct);

        Assert.Equal(GtfsImportOutcome.Imported, blocked.Outcome);
        Assert.Equal(1, blocked.StaleSnapshots);
        Assert.Equal(GtfsImportOutcome.Unchanged, retried.Outcome);
        Assert.Equal(0, retried.StaleSnapshots);
        Assert.Equal(7, await ScalarAsync<long>(database, PartitionCount));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void RenameRoute(Dictionary<string, string> files) =>
        files["routes.txt"] = "route_id,route_short_name,route_long_name,route_type\n1009,9,Renamed,0\n";

    private async Task<NpgsqlDataSource> MigratedDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        return database;
    }

    // The tram 9 slice zipped to a file, optionally edited first.
    private string Zip(string name, Action<Dictionary<string, string>>? edit = null)
    {
        var files = Directory.GetFiles(_tram9, "*.txt").ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
        edit?.Invoke(files);
        var path = Path.Combine(_directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (file, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file).Open());
            writer.Write(content);
        }

        return path;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource database, string sql)
    {
        await using var command = database.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}

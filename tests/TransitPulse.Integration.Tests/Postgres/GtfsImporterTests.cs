using System.IO.Compression;
using Npgsql;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class GtfsImporterTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly string _tram9 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gtfs", "hsl-tram-9");

    private readonly string _directory = Directory.CreateTempSubdirectory("gtfs-import-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fixture_snapshot_is_copied_row_for_row_into_an_inactive_snapshot()
    {
        var database = await MigratedDatabaseAsync();

        var result = await GtfsImporter.ImportAsync(database, "hsl", Zip("hsl.zip"), Ct);

        Assert.True(result.Report.IsValid, string.Join("; ", result.Report.Problems));
        Assert.NotNull(result.SnapshotId);
        // calendar_dates.txt of the slice has a header only.
        foreach (var table in (string[])["routes", "stops", "calendar", "shapes", "trips", "stop_times"])
        {
            Assert.Equal(result.Report.Rows[$"{table}.txt"], await ScalarAsync<long>(database, $"SELECT count(*) FROM gtfs.{table}"));
        }

        Assert.False(await ScalarAsync<bool>(database, "SELECT is_active FROM gtfs.snapshot"));
        // 25:35:00 of the service day: past midnight, still the trip's day.
        Assert.Equal(25 * 3600 + 35 * 60, await ScalarAsync<int>(database,
            "SELECT arrival_sec FROM gtfs.stop_times WHERE trip_id = '1009_20261030_Ke_2_2535' AND stop_sequence = 1"));
        // HSL writes parent_station as a single space: stored as NULL, not as " ".
        Assert.Equal(0, await ScalarAsync<long>(database, "SELECT count(*) FROM gtfs.stops WHERE parent_station IS NOT NULL"));
    }

    [Fact]
    public async Task Rejected_snapshot_leaves_no_snapshot_row_and_no_partitions()
    {
        var database = await MigratedDatabaseAsync();

        var result = await GtfsImporter.ImportAsync(database, "hsl", Zip("broken.zip", files => files["routes.txt"] = "route_id,route_type\n"), Ct);

        Assert.Null(result.SnapshotId);
        Assert.Contains("routes.txt has no rows", result.Report.Problems);
        Assert.Equal(0, await ScalarAsync<long>(database, "SELECT count(*) FROM gtfs.snapshot"));
        Assert.Equal(0, await ScalarAsync<long>(database,
            "SELECT count(*) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhparent JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'gtfs'"));
    }

    [Fact]
    public async Task Duplicate_stop_sequence_rejects_the_snapshot()
    {
        var database = await MigratedDatabaseAsync();

        var result = await GtfsImporter.ImportAsync(database, "hsl", Zip("duplicate.zip", files =>
        {
            var lines = files["stop_times.txt"].TrimEnd('\n').Split('\n');
            files["stop_times.txt"] = string.Join('\n', [.. lines, lines[1]]) + "\n";
        }), Ct);

        Assert.Null(result.SnapshotId);
        Assert.Contains(result.Report.Problems, problem => problem.StartsWith("stop_times.txt: duplicate key", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

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

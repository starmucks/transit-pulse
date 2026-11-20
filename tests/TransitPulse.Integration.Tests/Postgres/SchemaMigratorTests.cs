using Npgsql;
using TransitPulse.Infrastructure.Database;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class SchemaMigratorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_create_partitioned_gtfs_tables_and_a_second_run_applies_nothing()
    {
        var database = await postgres.CreateDatabaseAsync();

        var first = await SchemaMigrator.MigrateAsync(database, Ct);
        var second = await SchemaMigrator.MigrateAsync(database, Ct);

        Assert.Contains("0001_gtfs_schema", first);
        Assert.Contains("0002_stop_events", first);
        Assert.Empty(second);
        Assert.Equal(
            ["calendar", "calendar_dates", "routes", "shapes", "stop_times", "stops", "trips"],
            await PartitionedTablesAsync(database, "gtfs"));
        Assert.True(await TableExistsAsync(database, "public", "stop_events"));
    }

    [Fact]
    public async Task Concurrent_runs_apply_each_migration_once()
    {
        var database = await postgres.CreateDatabaseAsync();

        var runs = await Task.WhenAll(SchemaMigrator.MigrateAsync(database, Ct), SchemaMigrator.MigrateAsync(database, Ct));

        // Without the advisory lock the second run would fail on CREATE SCHEMA or apply the script twice.
        Assert.Single(runs, applied => applied.Count > 0);
        Assert.Single(runs, applied => applied.Count == 0);
    }

    private static async Task<List<string>> PartitionedTablesAsync(NpgsqlDataSource database, string schema)
    {
        await using var command = database.CreateCommand(
            """
            SELECT c.relname FROM pg_partitioned_table p
            JOIN pg_class c ON c.oid = p.partrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 ORDER BY c.relname
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlDataSource database, string schema, string table)
    {
        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = $1 AND table_name = $2
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        return await command.ExecuteScalarAsync(Ct) is not null;
    }
}

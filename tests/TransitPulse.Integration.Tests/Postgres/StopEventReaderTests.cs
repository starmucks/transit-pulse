using Npgsql;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.StopEvents;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>Stage 3.4: recent observed events by stop for the API stop panel.</summary>
public sealed class StopEventReaderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_create_the_stop_lookup_index()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);

        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM pg_indexes
            WHERE schemaname = 'public' AND indexname = 'stop_events_feed_stop_at'
            """);
        Assert.NotNull(await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task List_returns_empty_when_the_stop_has_no_events_in_the_window()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        await InsertAsync(
            database,
            ("e1", "1020453", "2026-11-05 08:00:00+00", 30));

        var reader = new StopEventReader(database);
        var since = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

        var rows = await reader.ListRecentByStopAsync("hsl", "1020453", since, limit: 50, Ct);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task List_returns_newest_first_within_since_and_limit()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        await InsertAsync(
            database,
            ("e1", "1020453", "2026-11-05 08:00:00+00", 30),
            ("e2", "1020453", "2026-11-05 08:10:00+00", null),
            ("e3", "1020453", "2026-11-05 08:20:00+00", 90),
            ("e4", "9999999", "2026-11-05 08:15:00+00", 0));

        var reader = new StopEventReader(database);
        var since = new DateTimeOffset(2026, 11, 5, 8, 5, 0, TimeSpan.Zero);

        var rows = await reader.ListRecentByStopAsync("hsl", "1020453", since, limit: 1, Ct);

        var only = Assert.Single(rows);
        Assert.Equal("e3", only.Id);
        Assert.Equal("1020453", only.StopId);
        Assert.Equal("arrival", only.Kind);
        Assert.Equal(90, only.DelaySec);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 8, 20, 0, TimeSpan.Zero), only.At);
    }

    private static async Task InsertAsync(
        NpgsqlDataSource database,
        params (string Id, string StopId, string At, int? DelaySec)[] rows)
    {
        foreach (var row in rows)
        {
            await using var insert = database.CreateCommand(
                """
                INSERT INTO public.stop_events (
                    id, feed_id, vehicle_id, trip_id, route_id, direction_id, service_date,
                    stop_id, stop_sequence, kind, at, delay_sec)
                VALUES (
                    $1, 'hsl', 'v1', 't1', '1009', 0, '2026-11-05',
                    $2, 1, 'arrival', $3::timestamptz, $4)
                """);
            insert.Parameters.Add(new NpgsqlParameter { Value = row.Id });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.StopId });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.At });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)row.DelaySec ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }
}

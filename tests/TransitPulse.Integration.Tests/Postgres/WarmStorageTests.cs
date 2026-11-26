using Npgsql;
using TransitPulse.Infrastructure.Database;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>Stage 3.1: hypertables, compression/retention policies, and the punctuality continuous aggregate.</summary>
public sealed class WarmStorageTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_create_hypertables_with_compression_and_retention_policies()
    {
        var database = await postgres.CreateDatabaseAsync();

        await SchemaMigrator.MigrateAsync(database, Ct);

        Assert.True(await IsHypertableAsync(database, "public", "vehicle_positions"));
        Assert.True(await IsHypertableAsync(database, "public", "stop_events"));
        Assert.Equal(["id", "at"], await PrimaryKeyColumnsAsync(database, "public", "stop_events"));
        Assert.True(await ColumnExistsAsync(database, "public", "stop_events", "delay_sec"));
        Assert.True(await IndexExistsAsync(database, "public", "vehicle_positions_feed_route_at"));

        Assert.Equal(TimeSpan.FromDays(2), await PolicyIntervalAsync(database, "policy_compression", "public.vehicle_positions", "compress_after"));
        Assert.Equal(TimeSpan.FromDays(14), await PolicyIntervalAsync(database, "policy_retention", "public.vehicle_positions", "drop_after"));
        Assert.Equal(TimeSpan.FromDays(7), await PolicyIntervalAsync(database, "policy_compression", "public.stop_events", "compress_after"));
        Assert.Equal(TimeSpan.FromDays(90), await PolicyIntervalAsync(database, "policy_retention", "public.stop_events", "drop_after"));
        Assert.True(await ContinuousAggregateExistsAsync(database, "punctuality_by_route_day"));
        Assert.True(await RefreshPolicyExistsAsync(database));
    }

    [Fact]
    public async Task Punctuality_aggregate_rolls_up_fixture_stop_events()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);

        await using (var insert = database.CreateCommand(
            """
            INSERT INTO public.stop_events (
                id, feed_id, vehicle_id, trip_id, route_id, direction_id, service_date,
                stop_id, stop_sequence, kind, at, delay_sec)
            VALUES
                ('e1', 'hsl', 'v1', 't1', '1009', 0, '2026-11-05', 's1', 1, 'arrival',
                 '2026-11-05 08:00:00+00', 30),
                ('e2', 'hsl', 'v1', 't1', '1009', 0, '2026-11-05', 's2', 2, 'arrival',
                 '2026-11-05 08:10:00+00', 240),
                ('e3', 'hsl', 'v2', 't2', '1009', 0, '2026-11-05', 's1', 1, 'departure',
                 '2026-11-05 08:01:00+00', NULL)
            """))
        {
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await ContinuousAggregateRefresh.RefreshPunctualityByRouteDayAsync(database, Ct);

        await using var query = database.CreateCommand(
            """
            SELECT arrivals, on_time_arrivals, avg_arrival_delay_sec
            FROM public.punctuality_by_route_day
            WHERE feed_id = 'hsl' AND route_id = '1009'
            """);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(135d, reader.GetDouble(2), 0.001);
        Assert.False(await reader.ReadAsync(Ct));
    }

    private static async Task<bool> IsHypertableAsync(NpgsqlDataSource database, string schema, string table)
    {
        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM timescaledb_information.hypertables
            WHERE hypertable_schema = $1 AND hypertable_name = $2
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        return await command.ExecuteScalarAsync(Ct) is not null;
    }

    private static async Task<TimeSpan> PolicyIntervalAsync(
        NpgsqlDataSource database, string procName, string relation, string key)
    {
        await using var command = database.CreateCommand(
            """
            SELECT (config ->> $3)::interval
            FROM timescaledb_information.jobs
            WHERE proc_name = $1 AND hypertable_schema || '.' || hypertable_name = $2
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = procName });
        command.Parameters.Add(new NpgsqlParameter { Value = relation });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct) || reader.IsDBNull(0))
        {
            throw new InvalidOperationException($"No {procName}.{key} for {relation}.");
        }

        return reader.GetFieldValue<TimeSpan>(0);
    }

    private static async Task<bool> ContinuousAggregateExistsAsync(NpgsqlDataSource database, string viewName)
    {
        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM timescaledb_information.continuous_aggregates
            WHERE view_name = $1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = viewName });
        return await command.ExecuteScalarAsync(Ct) is not null;
    }

    private static async Task<bool> RefreshPolicyExistsAsync(NpgsqlDataSource database)
    {
        // Jobs point at the materialization hypertable id, not the view name; one refresh policy is enough here.
        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_refresh_continuous_aggregate'
            """);
        return await command.ExecuteScalarAsync(Ct) is not null;
    }

    private static async Task<IReadOnlyList<string>> PrimaryKeyColumnsAsync(
        NpgsqlDataSource database, string schema, string table)
    {
        await using var command = database.CreateCommand(
            """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY (i.indkey)
            WHERE n.nspname = $1 AND c.relname = $2 AND i.indisprimary
            ORDER BY array_position(i.indkey, a.attnum)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var columns = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlDataSource database, string schema, string table, string column)
    {
        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = $1 AND table_name = $2 AND column_name = $3
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        command.Parameters.Add(new NpgsqlParameter { Value = column });
        return await command.ExecuteScalarAsync(Ct) is not null;
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlDataSource database, string schema, string index)
    {
        await using var command = database.CreateCommand(
            """
            SELECT 1 FROM pg_indexes
            WHERE schemaname = $1 AND indexname = $2
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        command.Parameters.Add(new NpgsqlParameter { Value = index });
        return await command.ExecuteScalarAsync(Ct) is not null;
    }
}

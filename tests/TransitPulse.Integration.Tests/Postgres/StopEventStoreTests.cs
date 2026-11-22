using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.StopEvents;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class StopEventStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VehicleStopEvent Arrival(DateTimeOffset at) => new(
        "hsl",
        "40/415",
        "1009_14",
        "1009",
        0,
        new DateOnly(2026, 11, 5),
        "1020453",
        1,
        StopEventKind.Arrival,
        at);

    [Fact]
    public async Task Upsert_keeps_one_row_when_the_same_event_is_written_twice()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var store = new StopEventStore(database);
        var first = Arrival(new DateTimeOffset(2026, 11, 5, 12, 0, 5, TimeSpan.Zero));
        var repeat = Arrival(new DateTimeOffset(2026, 11, 5, 12, 0, 6, TimeSpan.Zero));

        await store.UpsertAsync(first, Ct);
        await store.UpsertAsync(repeat, Ct);

        Assert.Equal((1, repeat.At), await CountAndAtAsync(database, first.Id));
    }

    [Fact]
    public async Task Upsert_keeps_one_row_when_at_moves_by_a_day_inside_the_window()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var store = new StopEventStore(database);
        var first = Arrival(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var drifted = Arrival(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));

        await store.UpsertAsync(first, Ct);
        await store.UpsertAsync(drifted, Ct);

        Assert.Equal((1, drifted.At), await CountAndAtAsync(database, first.Id));
    }

    [Fact]
    public async Task Concurrent_upserts_for_the_same_id_leave_one_row()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var store = new StopEventStore(database);
        var left = Arrival(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var right = Arrival(new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.Zero));

        await Task.WhenAll(store.UpsertAsync(left, Ct), store.UpsertAsync(right, Ct));

        await using var command = database.CreateCommand(
            "SELECT count(*)::int FROM public.stop_events WHERE id = $1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = left.Id });
        Assert.Equal(1, await command.ExecuteScalarAsync(Ct));
    }

    private static async Task<(int Count, DateTimeOffset At)> CountAndAtAsync(
        Npgsql.NpgsqlDataSource database, string id)
    {
        await using var command = database.CreateCommand(
            "SELECT count(*), min(at), max(at) FROM public.stop_events WHERE id = $1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
        return (reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1));
    }
}

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

        await using var command = database.CreateCommand(
            "SELECT count(*), min(at), max(at) FROM public.stop_events WHERE id = $1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = first.Id });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(repeat.At, reader.GetFieldValue<DateTimeOffset>(1));
        Assert.Equal(repeat.At, reader.GetFieldValue<DateTimeOffset>(2));
    }
}

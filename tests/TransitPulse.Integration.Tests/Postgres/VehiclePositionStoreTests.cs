using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Positions;

namespace TransitPulse.Integration.Tests.Postgres;

public sealed class VehiclePositionStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Insert_writes_one_row_and_ignores_the_same_primary_key()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var store = new VehiclePositionStore(database);
        var first = Enriched("40/415", new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero), 60.17, 24.94);
        var repeat = Enriched("40/415", first.Position.ObservedAt, 60.18, 24.95);

        Assert.Equal(1, await store.InsertManyAsync([first], Ct));
        Assert.Equal(0, await store.InsertManyAsync([repeat], Ct));
        Assert.Equal(1, await CountAsync(database, "hsl", "40/415"));
        Assert.Equal((60.17, 24.94), await CoordinatesAsync(database, "hsl", "40/415", first.Position.ObservedAt));
    }

    [Fact]
    public async Task Insert_keeps_distinct_observed_at_for_the_same_vehicle()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var store = new VehiclePositionStore(database);
        var t0 = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var batch = new[]
        {
            Enriched("40/415", t0, 60.17, 24.94),
            Enriched("40/415", t0.AddSeconds(10), 60.171, 24.941),
        };

        Assert.Equal(2, await store.InsertManyAsync(batch, Ct));
        Assert.Equal(2, await CountAsync(database, "hsl", "40/415"));
    }

    private static EnrichedPosition Enriched(string vehicleId, DateTimeOffset at, double lat, double lon) =>
        new(
            new VehiclePosition(
                "hsl",
                vehicleId,
                "1009_trip",
                "1009",
                lat,
                lon,
                8f,
                180f,
                45,
                null,
                at),
            new ScheduledProgress("1009_trip", "1009", 0, new DateOnly(2026, 11, 23), 1250.5, null),
            45);

    private static async Task<int> CountAsync(Npgsql.NpgsqlDataSource database, string feedId, string vehicleId)
    {
        await using var command = database.CreateCommand(
            "SELECT count(*)::int FROM public.vehicle_positions WHERE feed_id = $1 AND vehicle_id = $2");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = feedId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = vehicleId });
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<(double Lat, double Lon)> CoordinatesAsync(
        Npgsql.NpgsqlDataSource database, string feedId, string vehicleId, DateTimeOffset at)
    {
        await using var command = database.CreateCommand(
            """
            SELECT lat, lon FROM public.vehicle_positions
            WHERE feed_id = $1 AND vehicle_id = $2 AND observed_at = $3
            """);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = feedId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = vehicleId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = at });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetDouble(0), reader.GetDouble(1));
    }
}

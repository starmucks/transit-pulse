using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.StopEvents;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>Stage 3.4: route-day punctuality from the continuous aggregate.</summary>
public sealed class PunctualityReaderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_returns_zeros_when_the_route_day_has_no_aggregate_row()
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var reader = new PunctualityReader(database);

        var summary = await reader.GetRouteDayAsync("hsl", "1009", new DateOnly(2026, 11, 5), Ct);

        Assert.Equal(0, summary.Arrivals);
        Assert.Equal(0, summary.OnTimeArrivals);
        Assert.Null(summary.AvgArrivalDelaySec);
        Assert.Equal(PunctualityReader.OnTimeEarlySec, summary.OnTimeEarlySec);
        Assert.Equal(PunctualityReader.OnTimeLateSec, summary.OnTimeLateSec);
    }

    [Fact]
    public async Task Get_matches_the_refreshed_continuous_aggregate()
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

        await using (var refresh = database.CreateCommand(
            "CALL refresh_continuous_aggregate('public.punctuality_by_route_day', NULL, NULL)"))
        {
            await refresh.ExecuteNonQueryAsync(Ct);
        }

        var reader = new PunctualityReader(database);
        var summary = await reader.GetRouteDayAsync("hsl", "1009", new DateOnly(2026, 11, 5), Ct);

        Assert.Equal(2, summary.Arrivals);
        Assert.Equal(1, summary.OnTimeArrivals);
        Assert.Equal(135d, summary.AvgArrivalDelaySec!.Value, 0.001);
    }
}

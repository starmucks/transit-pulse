using Npgsql;
using TransitPulse.Domain;

namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>
/// Writes <see cref="VehicleStopEvent"/> rows into <c>public.stop_events</c>. Upsert by the deterministic event id so a
/// repeated Kafka delivery after a Processor restart does not create a second row (ADR-0014).
/// </summary>
public sealed class StopEventStore(NpgsqlDataSource dataSource)
{
    /// <summary>Inserts or updates one stop event; a second write with the same id refreshes <c>at</c> and <c>written_at</c>.</summary>
    /// <param name="stopEvent">Event from <c>events.stop</c>.</param>
    /// <param name="cancellationToken">Cancels the database call.</param>
    public async Task UpsertAsync(VehicleStopEvent stopEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stopEvent);
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.stop_events (
                id, feed_id, vehicle_id, trip_id, route_id, direction_id, service_date,
                stop_id, stop_sequence, kind, at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (id) DO UPDATE SET
                at = EXCLUDED.at,
                written_at = now()
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.FeedId });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.VehicleId });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.TripId });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.RouteId });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)stopEvent.DirectionId });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.ServiceDate });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.StopId });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.StopSequence });
        command.Parameters.Add(new NpgsqlParameter { Value = KindName(stopEvent.Kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = stopEvent.At });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string KindName(StopEventKind kind) => kind switch
    {
        StopEventKind.Arrival => "arrival",
        StopEventKind.Departure => "departure",
        StopEventKind.Passed => "passed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown stop event kind."),
    };
}

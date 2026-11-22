using Npgsql;
using TransitPulse.Domain;

namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>
/// Writes <see cref="VehicleStopEvent"/> rows into <c>public.stop_events</c>. A repeated Kafka delivery after a
/// Processor restart must not create a second row (ADR-0014).
/// </summary>
/// <remarks>
/// Timescale hypertables require every unique key to include the partition time column, so the table key is
/// <c>(id, at)</c>. The store takes a transaction advisory lock on the event id, deletes any prior row for that id
/// in a ±2 day window around the new <c>at</c> (chunk-pruned), then inserts — one row per id even under concurrent
/// writers. Repeats older than the compression window are not expected from Kafka (topic retention is shorter).
/// </remarks>
public sealed class StopEventStore(NpgsqlDataSource dataSource)
{
    /// <summary>Inserts or replaces one stop event so only one row remains for its deterministic id.</summary>
    /// <param name="stopEvent">Event from <c>events.stop</c>.</param>
    /// <param name="cancellationToken">Cancels the database call.</param>
    public async Task UpsertAsync(VehicleStopEvent stopEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stopEvent);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize writers for this id: without it two concurrent delete+insert pairs can both miss and insert.
        await using (var takeLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))",
            connection,
            transaction))
        {
            takeLock.Parameters.Add(new NpgsqlParameter { Value = stopEvent.Id });
            await takeLock.ExecuteNonQueryAsync(cancellationToken);
        }

        // Bound the delete by time so Timescale can prune chunks; event ids already embed the service day.
        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM public.stop_events
            WHERE id = $1
              AND at >= $2 - interval '2 days'
              AND at <  $2 + interval '2 days'
            """,
            connection,
            transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = stopEvent.Id });
            delete.Parameters.Add(new NpgsqlParameter { Value = stopEvent.At });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.stop_events (
                id, feed_id, vehicle_id, trip_id, route_id, direction_id, service_date,
                stop_id, stop_sequence, kind, at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """,
            connection,
            transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.Id });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.FeedId });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.VehicleId });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.TripId });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.RouteId });
            insert.Parameters.Add(new NpgsqlParameter { Value = (short)stopEvent.DirectionId });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.ServiceDate });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.StopId });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.StopSequence });
            insert.Parameters.Add(new NpgsqlParameter { Value = KindName(stopEvent.Kind) });
            insert.Parameters.Add(new NpgsqlParameter { Value = stopEvent.At });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string KindName(StopEventKind kind) => kind switch
    {
        StopEventKind.Arrival => "arrival",
        StopEventKind.Departure => "departure",
        StopEventKind.Passed => "passed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown stop event kind."),
    };
}

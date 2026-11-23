using Npgsql;
using NpgsqlTypes;
using TransitPulse.Domain;

namespace TransitPulse.Infrastructure.Positions;

/// <summary>Writes thinned enriched positions into Timescale.</summary>
public interface IVehiclePositionStore
{
    /// <summary>Inserts each position; returns how many rows were newly written (conflicts count as zero).</summary>
    Task<int> InsertManyAsync(IReadOnlyList<EnrichedPosition> positions, CancellationToken cancellationToken);
}

/// <summary>
/// Batch-inserts thinned enriched positions into <c>public.vehicle_positions</c>. Repeats of the same primary key are
/// ignored so at-least-once Kafka delivery does not bloat the series (ADR-0015).
/// </summary>
public sealed class VehiclePositionStore(NpgsqlDataSource dataSource) : IVehiclePositionStore
{
    /// <summary>
    /// Inserts each position; returns how many rows were newly written (conflicts count as zero).
    /// </summary>
    /// <param name="positions">Thinned points to persist; empty is a no-op.</param>
    /// <param name="cancellationToken">Cancels the database call.</param>
    public async Task<int> InsertManyAsync(
        IReadOnlyList<EnrichedPosition> positions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count == 0)
        {
            return 0;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var written = 0;

        foreach (var enriched in positions)
        {
            ArgumentNullException.ThrowIfNull(enriched);
            var position = enriched.Position;
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO public.vehicle_positions (
                    feed_id, vehicle_id, observed_at, lon, lat, bearing, speed_mps,
                    route_id, trip_id, delay_sec, along_route_m)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (feed_id, vehicle_id, observed_at) DO NOTHING
                """,
                connection,
                transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = position.FeedId });
            insert.Parameters.Add(new NpgsqlParameter { Value = position.VehicleId });
            insert.Parameters.Add(new NpgsqlParameter { Value = position.ObservedAt });
            insert.Parameters.Add(new NpgsqlParameter { Value = position.Lon });
            insert.Parameters.Add(new NpgsqlParameter { Value = position.Lat });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)position.Bearing ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Real });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)position.SpeedMps ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Real });
            insert.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)(enriched.Schedule?.RouteId ?? position.RouteId) ?? DBNull.Value,
            });
            insert.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)(enriched.Schedule?.TripId ?? position.TripId) ?? DBNull.Value,
            });
            insert.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)enriched.DelaySec ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Integer,
            });
            insert.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)enriched.Schedule?.AlongRouteMetres ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Double,
            });
            written += await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return written;
    }
}

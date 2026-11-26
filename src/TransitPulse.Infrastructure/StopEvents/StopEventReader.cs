using Npgsql;

namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>Reads recent rows from <c>public.stop_events</c> for one stop.</summary>
/// <param name="dataSource">Database with warm stop events.</param>
public sealed class StopEventReader(NpgsqlDataSource dataSource) : IStopEventReader
{
    private const string RecentByStop =
        """
        SELECT id, vehicle_id, trip_id, route_id, direction_id, service_date,
               stop_id, stop_sequence, kind, at, delay_sec
        FROM public.stop_events
        WHERE feed_id = $1
          AND stop_id = $2
          AND at >= $3
        ORDER BY at DESC
        LIMIT $4
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObservedStopEvent>> ListRecentByStopAsync(
        string feedId,
        string stopId,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stopId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RecentByStop, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = feedId });
        command.Parameters.Add(new NpgsqlParameter { Value = stopId });
        command.Parameters.Add(new NpgsqlParameter { Value = since });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ObservedStopEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ObservedStopEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt16(4),
                DateOnly.FromDateTime(reader.GetFieldValue<DateTime>(5)),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        }

        return rows;
    }
}

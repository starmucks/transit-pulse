using Npgsql;

namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>Reads <c>public.punctuality_by_route_day</c> for the route page.</summary>
/// <param name="dataSource">Database with the continuous aggregate from migration 0003.</param>
/// <remarks>
/// On-time window [-60, 180] s matches the CAGG definition in <c>0003_warm_storage.sql</c>; changing it requires a
/// new migration of the view, not only these constants.
/// </remarks>
public sealed class PunctualityReader(NpgsqlDataSource dataSource) : IPunctualityReader
{
    /// <summary>Inclusive early bound (seconds) used by the continuous aggregate.</summary>
    public const int OnTimeEarlySec = -60;

    /// <summary>Inclusive late bound (seconds) used by the continuous aggregate.</summary>
    public const int OnTimeLateSec = 180;

    private const string RouteDay =
        """
        SELECT arrivals, on_time_arrivals, avg_arrival_delay_sec
        FROM public.punctuality_by_route_day
        WHERE feed_id = $1
          AND route_id = $2
          AND day = $3::timestamptz
        """;

    /// <inheritdoc />
    public async Task<RoutePunctualityDay> GetRouteDayAsync(
        string feedId,
        string routeId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RouteDay, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = feedId });
        command.Parameters.Add(new NpgsqlParameter { Value = routeId });
        command.Parameters.Add(new NpgsqlParameter { Value = dayStart });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Empty(feedId, routeId, day);
        }

        return new RoutePunctualityDay(
            day,
            feedId,
            routeId,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            OnTimeEarlySec,
            OnTimeLateSec);
    }

    private static RoutePunctualityDay Empty(string feedId, string routeId, DateOnly day) =>
        new(day, feedId, routeId, 0, 0, null, OnTimeEarlySec, OnTimeLateSec);
}

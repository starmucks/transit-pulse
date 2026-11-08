using System.Runtime.InteropServices;
using Npgsql;
using TransitPulse.Domain;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>A feed's schedule as read from one snapshot.</summary>
/// <param name="SnapshotId">The snapshot it was read from; a different active one means it is out of date.</param>
/// <param name="Schedule">The scheduled runs of the requested days.</param>
public sealed record LoadedTripSchedule(int SnapshotId, TripSchedule Schedule);

/// <summary>Reads the runs of a few service days from a feed's active GTFS snapshot.</summary>
/// <param name="dataSource">Database with the <c>gtfs</c> schema.</param>
public sealed class TripScheduleReader(NpgsqlDataSource dataSource)
{
    // A background read of a few seconds, up to 25 s measured on a busy database: the default 30 s leaves too little margin,
    // and a read that times out would only start over a minute later.
    private const int ReadTimeoutSeconds = 120;

    // A day's services are the calendar's, minus the days calendar_dates removes, plus the days it adds. A run without
    // a direction or a first departure cannot be found by a descriptor anyway; neither occurs in HSL snapshots.
    //
    // MATERIALIZED fixes the order: runs first, their first stops after. Otherwise statistics decide, and after an
    // ANALYZE the planner may look up the first stop of all 375 000 trips of an HSL snapshot instead of the 71 000 runs
    // wanted (measured, docs/benchmarks.md "Trip lookup"), before returning a single row.
    private const string RunsOfDays =
        """
        WITH days AS (
            SELECT unnest($2::date[]) AS date
        ), services AS (
            SELECT d.date, c.service_id
            FROM days d
            JOIN gtfs.calendar c ON c.snapshot_id = $1
                AND d.date BETWEEN c.start_date AND c.end_date
                AND (c.days::int & (1 << (extract(isodow FROM d.date)::int - 1))) <> 0
            WHERE NOT EXISTS (
                SELECT FROM gtfs.calendar_dates x
                WHERE x.snapshot_id = $1 AND x.service_id = c.service_id AND x.date = d.date AND NOT x.added)
            UNION
            SELECT d.date, x.service_id
            FROM days d
            JOIN gtfs.calendar_dates x ON x.snapshot_id = $1 AND x.date = d.date AND x.added
        ), runs AS MATERIALIZED (
            SELECT s.date, t.trip_id, t.route_id, t.direction_id, t.shape_id
            FROM services s
            JOIN gtfs.trips t ON t.snapshot_id = $1 AND t.service_id = s.service_id
            WHERE t.direction_id IS NOT NULL
        )
        SELECT r.date, r.trip_id, r.route_id, r.direction_id, first_call.departure_sec, r.shape_id
        FROM runs r
        JOIN LATERAL (
            SELECT st.departure_sec
            FROM gtfs.stop_times st
            WHERE st.snapshot_id = $1 AND st.trip_id = r.trip_id
            ORDER BY st.stop_sequence
            LIMIT 1
        ) first_call ON true
        WHERE first_call.departure_sec IS NOT NULL
        """;

    // Separate from the runs: joined to them, each shape's points would come once per run, tens of thousands of times.
    // The order is the primary key's, so the points of a shape arrive together and in sequence.
    private const string PointsOfShapes =
        "SELECT shape_id, lat, lon FROM gtfs.shapes WHERE snapshot_id = $1 AND shape_id = ANY($2) ORDER BY shape_id, sequence";

    /// <summary>Finds the feed's active snapshot: a cheap way to learn whether a loaded schedule is out of date.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="cancellationToken">Stops the query.</param>
    /// <returns>The snapshot id, or <see langword="null"/> when nothing has been imported for the feed.</returns>
    public async Task<int?> ActiveSnapshotAsync(string feedId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ActiveSnapshotAsync(connection, feedId, cancellationToken);
    }

    /// <summary>Reads the runs of the given days from the feed's active snapshot.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="serviceDates">Service days to read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The schedule, or <see langword="null"/> when nothing has been imported for the feed.</returns>
    public async Task<LoadedTripSchedule?> ReadAsync(
        string feedId, IReadOnlyCollection<DateOnly> serviceDates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceDates);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Taken before choosing the snapshot: an import that activates a newer one could otherwise drop this one's
        // partitions between the two statements, and the read would quietly return nothing. The importer's drop uses
        // NOWAIT and tries again later, and ACCESS SHARE does not stop an import from attaching new partitions.
        await using (var keep = new NpgsqlCommand(
            "LOCK TABLE gtfs.calendar, gtfs.calendar_dates, gtfs.trips, gtfs.stop_times, gtfs.shapes IN ACCESS SHARE MODE",
            connection,
            transaction))
        {
            await keep.ExecuteNonQueryAsync(cancellationToken);
        }

        if (await ActiveSnapshotAsync(connection, feedId, cancellationToken) is not { } snapshotId)
        {
            return null;
        }

        var trips = new List<ScheduledTrip>();
        var shapeIds = new List<string?>();
        await using (var select = new NpgsqlCommand(RunsOfDays, connection, transaction) { CommandTimeout = ReadTimeoutSeconds })
        {
            select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
            select.Parameters.Add(new NpgsqlParameter { Value = serviceDates.ToArray() });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                trips.Add(new ScheduledTrip(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt16(3),
                    reader.GetInt32(4),
                    reader.GetFieldValue<DateOnly>(0)));
                shapeIds.Add(reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        var shapes = await ReadShapesAsync(
            connection, transaction, snapshotId, [.. shapeIds.OfType<string>().Distinct()], cancellationToken);
        for (var i = 0; i < trips.Count; i++)
        {
            if (shapeIds[i] is { } shapeId && shapes.TryGetValue(shapeId, out var shape))
            {
                trips[i] = trips[i] with { Shape = shape };
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new LoadedTripSchedule(snapshotId, new TripSchedule(serviceDates, trips));
    }

    private static async Task<Dictionary<string, RouteShape>> ReadShapesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int snapshotId, string[] shapeIds, CancellationToken cancellationToken)
    {
        var shapes = new Dictionary<string, RouteShape>(shapeIds.Length, StringComparer.Ordinal);
        var lats = new List<double>();
        var lons = new List<double>();
        string? current = null;

        await using var select = new NpgsqlCommand(PointsOfShapes, connection, transaction) { CommandTimeout = ReadTimeoutSeconds };
        select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
        select.Parameters.Add(new NpgsqlParameter { Value = shapeIds });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var shapeId = reader.GetString(0);
            if (shapeId != current)
            {
                AddShape(shapes, current, lats, lons);
                current = shapeId;
            }

            lats.Add(reader.GetDouble(1));
            lons.Add(reader.GetDouble(2));
        }

        AddShape(shapes, current, lats, lons);
        return shapes;
    }

    private static void AddShape(Dictionary<string, RouteShape> shapes, string? shapeId, List<double> lats, List<double> lons)
    {
        // A single point is no line to project onto: its trips go without a shape rather than failing the whole read.
        if (shapeId is not null && lats.Count >= 2)
        {
            shapes.Add(shapeId, new RouteShape(CollectionsMarshal.AsSpan(lats), CollectionsMarshal.AsSpan(lons)));
        }

        lats.Clear();
        lons.Clear();
    }

    private static async Task<int?> ActiveSnapshotAsync(NpgsqlConnection connection, string feedId, CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand("SELECT snapshot_id FROM gtfs.snapshot WHERE feed_id = $1 AND is_active", connection);
        select.Parameters.Add(new NpgsqlParameter { Value = feedId });
        return await select.ExecuteScalarAsync(cancellationToken) as int?;
    }
}

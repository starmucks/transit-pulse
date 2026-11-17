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

    // One row per trip instead of one per call: trips through the same stops in the same order get the same key, so only
    // one trip of each pattern has its stops read (HSL: 1 203 patterns for 380 543 trips). Separate from the runs query so
    // that its measured plan stays as it is.
    //
    // Per trip through the primary key, not GROUP BY over trip_id = ANY(...): that scans the snapshot's whole stop_times and
    // sorts 1.6 million rows on disk, reading 96 529 blocks against 10 861 here (docs/benchmarks.md "Stops in the schedule").
    private const string StopKeysOfTrips =
        """
        SELECT t.trip_id, k.stop_key
        FROM unnest($2::text[]) AS t(trip_id)
        JOIN LATERAL (
            SELECT md5(string_agg(st.stop_id, ',' ORDER BY st.stop_sequence)) AS stop_key
            FROM gtfs.stop_times st
            WHERE st.snapshot_id = $1 AND st.trip_id = t.trip_id
        ) k ON k.stop_key IS NOT NULL
        """;

    // A stop without coordinates cannot be placed on the line: it is left out of its pattern.
    private const string StopsOfTrips =
        """
        SELECT st.trip_id, st.stop_sequence, st.stop_id, s.lat, s.lon
        FROM gtfs.stop_times st
        JOIN gtfs.stops s ON s.snapshot_id = st.snapshot_id AND s.stop_id = st.stop_id
        WHERE st.snapshot_id = $1 AND st.trip_id = ANY($2) AND s.lat IS NOT NULL AND s.lon IS NOT NULL
        ORDER BY st.trip_id, st.stop_sequence
        """;

    // Unlike stop geometry, times cannot be shared by every trip through the same stops: running times vary during the day.
    // LATERAL makes one primary-key range scan per trip instead of a scan and sort of the snapshot's whole stop_times table.
    private const string StopTimesOfTrips =
        """
        SELECT t.trip_id, st.stop_sequence, st.arrival_sec, st.departure_sec
        FROM unnest($2::text[]) WITH ORDINALITY AS t(trip_id, ordinal)
        JOIN LATERAL (
            SELECT s.stop_sequence, s.arrival_sec, s.departure_sec
            FROM gtfs.stop_times s
            WHERE s.snapshot_id = $1 AND s.trip_id = t.trip_id
            ORDER BY s.stop_sequence
        ) st ON true
        ORDER BY t.ordinal, st.stop_sequence
        """;

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

        // A trip's shape is the trip's own, the same on every day it runs: one entry per trip id.
        var shapeOfTrip = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < trips.Count; i++)
        {
            if (shapeIds[i] is { } shapeId && shapes.ContainsKey(shapeId))
            {
                shapeOfTrip.TryAdd(trips[i].TripId, shapeId);
            }
        }

        var stops = await ReadStopPatternsAsync(connection, transaction, snapshotId, shapeOfTrip, shapes, cancellationToken);
        var timetables = await ReadStopTimetablesAsync(connection, transaction, snapshotId, shapeOfTrip.Keys, cancellationToken);
        for (var i = 0; i < trips.Count; i++)
        {
            if (shapeOfTrip.TryGetValue(trips[i].TripId, out var shapeId))
            {
                trips[i] = trips[i] with
                {
                    Shape = shapes[shapeId],
                    Stops = stops.GetValueOrDefault(trips[i].TripId),
                    Timetable = timetables.GetValueOrDefault(trips[i].TripId),
                };
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

    // Trips with the same shape and the same stop key share one pattern, built from the stops of the first such trip.
    private static async Task<Dictionary<string, StopPattern>> ReadStopPatternsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int snapshotId,
        Dictionary<string, string> shapeOfTrip,
        Dictionary<string, RouteShape> shapes,
        CancellationToken cancellationToken)
    {
        var patternOfTrip = new Dictionary<string, (string ShapeId, string StopKey)>(shapeOfTrip.Count, StringComparer.Ordinal);
        var firstTripOf = new Dictionary<(string ShapeId, string StopKey), string>();
        await using (var select = new NpgsqlCommand(StopKeysOfTrips, connection, transaction) { CommandTimeout = ReadTimeoutSeconds })
        {
            select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
            select.Parameters.Add(new NpgsqlParameter { Value = shapeOfTrip.Keys.ToArray() });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tripId = reader.GetString(0);
                var pattern = (shapeOfTrip[tripId], reader.GetString(1));
                patternOfTrip.Add(tripId, pattern);
                firstTripOf.TryAdd(pattern, tripId);
            }
        }

        var built = new Dictionary<(string ShapeId, string StopKey), StopPattern>(firstTripOf.Count);
        var patternOfFirstTrip = firstTripOf.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var points = new List<StopPoint>();
        string? current = null;
        await using (var select = new NpgsqlCommand(StopsOfTrips, connection, transaction) { CommandTimeout = ReadTimeoutSeconds })
        {
            select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
            select.Parameters.Add(new NpgsqlParameter { Value = patternOfFirstTrip.Keys.ToArray() });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tripId = reader.GetString(0);
                if (tripId != current)
                {
                    AddPattern(built, current, points, patternOfFirstTrip, shapes);
                    current = tripId;
                }

                points.Add(new StopPoint(reader.GetString(2), reader.GetInt32(1), reader.GetDouble(3), reader.GetDouble(4)));
            }
        }

        AddPattern(built, current, points, patternOfFirstTrip, shapes);

        // A pattern whose stops all lack coordinates was never built: its trips go without stops.
        var stops = new Dictionary<string, StopPattern>(patternOfTrip.Count, StringComparer.Ordinal);
        foreach (var (tripId, pattern) in patternOfTrip)
        {
            if (built.TryGetValue(pattern, out var stopPattern))
            {
                stops.Add(tripId, stopPattern);
            }
        }

        return stops;
    }

    private static async Task<Dictionary<string, StopTimetable>> ReadStopTimetablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int snapshotId,
        IEnumerable<string> tripIds,
        CancellationToken cancellationToken)
    {
        var ids = tripIds.ToArray();
        var timetables = new Dictionary<string, StopTimetable>(ids.Length, StringComparer.Ordinal);
        var calls = new List<ScheduledStopTime>();
        string? current = null;
        await using var select = new NpgsqlCommand(StopTimesOfTrips, connection, transaction) { CommandTimeout = ReadTimeoutSeconds };
        select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
        select.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tripId = reader.GetString(0);
            if (tripId != current)
            {
                AddTimetable(timetables, current, calls);
                current = tripId;
            }

            calls.Add(new ScheduledStopTime(
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        AddTimetable(timetables, current, calls);
        return timetables;
    }

    private static void AddPattern(
        Dictionary<(string ShapeId, string StopKey), StopPattern> built,
        string? tripId,
        List<StopPoint> points,
        Dictionary<string, (string ShapeId, string StopKey)> patternOfFirstTrip,
        Dictionary<string, RouteShape> shapes)
    {
        if (tripId is not null)
        {
            var pattern = patternOfFirstTrip[tripId];
            built.Add(pattern, new StopPattern(shapes[pattern.ShapeId], CollectionsMarshal.AsSpan(points)));
        }

        points.Clear();
    }

    private static void AddTimetable(Dictionary<string, StopTimetable> timetables, string? tripId, List<ScheduledStopTime> calls)
    {
        if (tripId is not null)
        {
            timetables.Add(tripId, new StopTimetable(CollectionsMarshal.AsSpan(calls)));
        }

        calls.Clear();
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

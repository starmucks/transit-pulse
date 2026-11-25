using Npgsql;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>Ordered points of one shape used by a route (catalogue view for the map).</summary>
/// <param name="ShapeId">GTFS shape id.</param>
/// <param name="Points">Vertices in sequence order.</param>
public sealed record GtfsRouteShape(string ShapeId, IReadOnlyList<ShapeCoordinate> Points);

/// <summary>One WGS 84 point on a route shape.</summary>
/// <param name="Lat">Latitude.</param>
/// <param name="Lon">Longitude.</param>
public readonly record struct ShapeCoordinate(double Lat, double Lon);

/// <summary>Result of listing shapes for one route.</summary>
/// <param name="RouteFound"><see langword="true"/> when the route exists in the chosen snapshot.</param>
/// <param name="Shapes">Shapes of the route; empty when none or when the route is missing.</param>
public sealed record RouteShapesResult(bool RouteFound, IReadOnlyList<GtfsRouteShape> Shapes);

/// <summary>Reads routes, stops and route shapes from a feed's GTFS snapshot.</summary>
/// <param name="dataSource">Database with the <c>gtfs</c> schema.</param>
/// <remarks>
/// Same lock-before-read rule as <see cref="TripScheduleReader"/> (ADR-0008): an import must not drop partitions
/// between choosing a snapshot and reading its rows. Prefer the overloads that take a fixed
/// <c>snapshotId</c> when the caller (e.g. HybridCache) has already pinned one.
/// </remarks>
public sealed class GtfsCatalogueReader(NpgsqlDataSource dataSource) : IGtfsCatalogueReader
{
    private const string RoutesOfSnapshot =
        """
        SELECT route_id, short_name, long_name, route_type
        FROM gtfs.routes
        WHERE snapshot_id = $1
        ORDER BY short_name NULLS LAST, route_id
        """;

    // Stops without coordinates cannot be drawn on the map; keep stations and platforms that have them.
    private const string StopsOfSnapshot =
        """
        SELECT stop_id, name, lat, lon, location_type, parent_station
        FROM gtfs.stops
        WHERE snapshot_id = $1 AND lat IS NOT NULL AND lon IS NOT NULL
        ORDER BY stop_id
        """;

    private const string RouteExists =
        "SELECT EXISTS(SELECT 1 FROM gtfs.routes WHERE snapshot_id = $1 AND route_id = $2)";

    private const string ShapeIdsOfRoute =
        """
        SELECT DISTINCT shape_id
        FROM gtfs.trips
        WHERE snapshot_id = $1 AND route_id = $2 AND shape_id IS NOT NULL
        ORDER BY shape_id
        """;

    private const string PointsOfShapes =
        "SELECT shape_id, lat, lon FROM gtfs.shapes WHERE snapshot_id = $1 AND shape_id = ANY($2) ORDER BY shape_id, sequence";

    private const string SnapshotExists = "SELECT EXISTS(SELECT 1 FROM gtfs.snapshot WHERE snapshot_id = $1)";

    /// <summary>Finds the feed's active snapshot id without reading catalogue rows.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="cancellationToken">Stops the query.</param>
    /// <returns>The snapshot id, or <see langword="null"/> when nothing has been imported for the feed.</returns>
    public async Task<int?> ActiveSnapshotAsync(string feedId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ActiveSnapshotAsync(connection, feedId, cancellationToken);
    }

    /// <summary>Lists every route in the feed's active snapshot.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The routes, or <see langword="null"/> when nothing has been imported for the feed.</returns>
    public Task<IReadOnlyList<GtfsRoute>?> ListRoutesAsync(string feedId, CancellationToken cancellationToken) =>
        ReadActiveCatalogueAsync(feedId, ReadRoutesAsync, cancellationToken);

    /// <summary>Lists every route in a fixed snapshot (for cache keys that already pinned the id).</summary>
    /// <param name="snapshotId">Snapshot to read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The routes, or <see langword="null"/> when that snapshot no longer exists.</returns>
    public Task<IReadOnlyList<GtfsRoute>?> ListRoutesAsync(int snapshotId, CancellationToken cancellationToken) =>
        ReadFixedCatalogueAsync(snapshotId, ReadRoutesAsync, cancellationToken);

    /// <summary>Lists every stop with coordinates in the feed's active snapshot.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The stops, or <see langword="null"/> when nothing has been imported for the feed.</returns>
    public Task<IReadOnlyList<GtfsStop>?> ListStopsAsync(string feedId, CancellationToken cancellationToken) =>
        ReadActiveCatalogueAsync(feedId, ReadStopsAsync, cancellationToken);

    /// <summary>Lists every stop with coordinates in a fixed snapshot.</summary>
    /// <param name="snapshotId">Snapshot to read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The stops, or <see langword="null"/> when that snapshot no longer exists.</returns>
    public Task<IReadOnlyList<GtfsStop>?> ListStopsAsync(int snapshotId, CancellationToken cancellationToken) =>
        ReadFixedCatalogueAsync(snapshotId, ReadStopsAsync, cancellationToken);

    /// <summary>Lists the distinct shapes of a route in the feed's active snapshot.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="routeId">Agency route id.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// The shapes (possibly empty when the route has none), <see langword="null"/> when the feed has no active
    /// snapshot, or <see cref="RouteShapesResult.RouteFound"/> false when the route is missing.
    /// </returns>
    public async Task<RouteShapesResult?> ListRouteShapesAsync(
        string feedId, string routeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCatalogueAsync(connection, transaction, cancellationToken);

        if (await ActiveSnapshotAsync(connection, feedId, cancellationToken) is not { } snapshotId)
        {
            return null;
        }

        return await ReadRouteShapesAsync(connection, transaction, snapshotId, routeId, cancellationToken);
    }

    /// <summary>Lists the distinct shapes of a route in a fixed snapshot.</summary>
    /// <param name="snapshotId">Snapshot to read.</param>
    /// <param name="routeId">Agency route id.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// The shapes, or <see langword="null"/> when that snapshot no longer exists.
    /// </returns>
    public async Task<RouteShapesResult?> ListRouteShapesAsync(
        int snapshotId, string routeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCatalogueAsync(connection, transaction, cancellationToken);

        if (!await SnapshotExistsAsync(connection, transaction, snapshotId, cancellationToken))
        {
            return null;
        }

        return await ReadRouteShapesAsync(connection, transaction, snapshotId, routeId, cancellationToken);
    }

    private async Task<T?> ReadActiveCatalogueAsync<T>(
        string feedId,
        Func<NpgsqlConnection, NpgsqlTransaction, int, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCatalogueAsync(connection, transaction, cancellationToken);

        if (await ActiveSnapshotAsync(connection, feedId, cancellationToken) is not { } snapshotId)
        {
            return null;
        }

        return await read(connection, transaction, snapshotId, cancellationToken);
    }

    private async Task<T?> ReadFixedCatalogueAsync<T>(
        int snapshotId,
        Func<NpgsqlConnection, NpgsqlTransaction, int, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCatalogueAsync(connection, transaction, cancellationToken);

        if (!await SnapshotExistsAsync(connection, transaction, snapshotId, cancellationToken))
        {
            return null;
        }

        return await read(connection, transaction, snapshotId, cancellationToken);
    }

    private static async Task<RouteShapesResult> ReadRouteShapesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int snapshotId,
        string routeId,
        CancellationToken cancellationToken)
    {
        if (!await RouteExistsAsync(connection, transaction, snapshotId, routeId, cancellationToken))
        {
            return new RouteShapesResult(RouteFound: false, Shapes: []);
        }

        var shapes = await ReadShapesAsync(connection, transaction, snapshotId, routeId, cancellationToken);
        return new RouteShapesResult(RouteFound: true, Shapes: shapes);
    }

    private static async Task LockCatalogueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Taken before choosing or verifying the snapshot: same reason as TripScheduleReader (ADR-0008).
        await using var keep = new NpgsqlCommand(
            "LOCK TABLE gtfs.routes, gtfs.stops, gtfs.trips, gtfs.shapes IN ACCESS SHARE MODE",
            connection,
            transaction);
        await keep.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> SnapshotExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int snapshotId,
        CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand(SnapshotExists, connection, transaction);
        select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
        return (bool)(await select.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<IReadOnlyList<GtfsRoute>> ReadRoutesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int snapshotId, CancellationToken cancellationToken)
    {
        var routes = new List<GtfsRoute>();
        await using var select = new NpgsqlCommand(RoutesOfSnapshot, connection, transaction);
        select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            routes.Add(new GtfsRoute(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)));
        }

        return routes;
    }

    private static async Task<IReadOnlyList<GtfsStop>> ReadStopsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int snapshotId, CancellationToken cancellationToken)
    {
        var stops = new List<GtfsStop>();
        await using var select = new NpgsqlCommand(StopsOfSnapshot, connection, transaction);
        select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stops.Add(new GtfsStop(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt16(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return stops;
    }

    private static async Task<bool> RouteExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int snapshotId,
        string routeId,
        CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand(RouteExists, connection, transaction);
        select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
        select.Parameters.Add(new NpgsqlParameter { Value = routeId });
        return (bool)(await select.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<IReadOnlyList<GtfsRouteShape>> ReadShapesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int snapshotId,
        string routeId,
        CancellationToken cancellationToken)
    {
        var shapeIds = new List<string>();
        await using (var select = new NpgsqlCommand(ShapeIdsOfRoute, connection, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
            select.Parameters.Add(new NpgsqlParameter { Value = routeId });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                shapeIds.Add(reader.GetString(0));
            }
        }

        if (shapeIds.Count == 0)
        {
            return [];
        }

        var byId = new Dictionary<string, List<ShapeCoordinate>>(StringComparer.Ordinal);
        await using (var select = new NpgsqlCommand(PointsOfShapes, connection, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = snapshotId });
            select.Parameters.Add(new NpgsqlParameter { Value = shapeIds.ToArray() });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var shapeId = reader.GetString(0);
                if (!byId.TryGetValue(shapeId, out var points))
                {
                    points = [];
                    byId[shapeId] = points;
                }

                points.Add(new ShapeCoordinate(reader.GetDouble(1), reader.GetDouble(2)));
            }
        }

        // Preserve DISTINCT … ORDER BY shape_id order; a shape listed on trips but missing points is still returned empty.
        return shapeIds
            .Select(id => new GtfsRouteShape(id, byId.TryGetValue(id, out var points) ? points : []))
            .ToArray();
    }

    private static async Task<int?> ActiveSnapshotAsync(
        NpgsqlConnection connection, string feedId, CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand(
            "SELECT snapshot_id FROM gtfs.snapshot WHERE feed_id = $1 AND is_active", connection);
        select.Parameters.Add(new NpgsqlParameter { Value = feedId });
        return await select.ExecuteScalarAsync(cancellationToken) as int?;
    }
}

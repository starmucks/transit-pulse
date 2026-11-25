namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>Read surface the API cache uses: active snapshot lookup and fixed-id catalogue rows.</summary>
public interface IGtfsCatalogueReader
{
    /// <summary>Finds the feed's active snapshot id without reading catalogue rows.</summary>
    Task<int?> ActiveSnapshotAsync(string feedId, CancellationToken cancellationToken);

    /// <summary>Lists every route in a fixed snapshot, or <see langword="null"/> when it no longer exists.</summary>
    Task<IReadOnlyList<GtfsRoute>?> ListRoutesAsync(int snapshotId, CancellationToken cancellationToken);

    /// <summary>Lists every stop with coordinates in a fixed snapshot, or <see langword="null"/> when it no longer exists.</summary>
    Task<IReadOnlyList<GtfsStop>?> ListStopsAsync(int snapshotId, CancellationToken cancellationToken);

    /// <summary>Lists shapes of a route in a fixed snapshot, or <see langword="null"/> when the snapshot no longer exists.</summary>
    Task<RouteShapesResult?> ListRouteShapesAsync(int snapshotId, string routeId, CancellationToken cancellationToken);
}

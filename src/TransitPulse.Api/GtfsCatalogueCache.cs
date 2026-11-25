using Microsoft.Extensions.Caching.Hybrid;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Api;

/// <summary>
/// Caches stable GTFS catalogue reads in <see cref="HybridCache"/> (L1 memory + L2 Redis).
/// Keys include the active snapshot id so a new import starts a fresh entry instead of serving stale rows.
/// Factories read that same fixed id — they must not re-resolve "active" (which can race with an import).
/// </summary>
public sealed class GtfsCatalogueCache(GtfsCatalogueReader reader, HybridCache cache)
{
    // Catalogue changes only on import; snapshot id in the key already isolates versions.
    private static readonly HybridCacheEntryOptions _entry = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
    };

    /// <summary>Returns cached routes for the feed's active snapshot, or <see langword="null"/> when none is active.</summary>
    public async Task<IReadOnlyList<GtfsRoute>?> GetRoutesAsync(string feedId, CancellationToken cancellationToken)
    {
        if (await reader.ActiveSnapshotAsync(feedId, cancellationToken) is not { } snapshotId)
        {
            // Do not cache "no snapshot": the next import must be visible immediately.
            return null;
        }

        // Concrete arrays: HybridCache L2 (Redis) round-trips through System.Text.Json, which cannot materialise interfaces.
        return await cache.GetOrCreateAsync(
            $"gtfs:{feedId}:{snapshotId}:routes",
            async cancel => (await reader.ListRoutesAsync(snapshotId, cancel) ?? []).ToArray(),
            _entry,
            cancellationToken: cancellationToken);
    }

    /// <summary>Returns cached stops for the feed's active snapshot, or <see langword="null"/> when none is active.</summary>
    public async Task<IReadOnlyList<GtfsStop>?> GetStopsAsync(string feedId, CancellationToken cancellationToken)
    {
        if (await reader.ActiveSnapshotAsync(feedId, cancellationToken) is not { } snapshotId)
        {
            return null;
        }

        return await cache.GetOrCreateAsync(
            $"gtfs:{feedId}:{snapshotId}:stops",
            async cancel => (await reader.ListStopsAsync(snapshotId, cancel) ?? []).ToArray(),
            _entry,
            cancellationToken: cancellationToken);
    }

    /// <summary>Returns cached shapes for one route, or <see langword="null"/> when the feed has no active snapshot.</summary>
    public async Task<RouteShapesResult?> GetRouteShapesAsync(
        string feedId, string routeId, CancellationToken cancellationToken)
    {
        if (await reader.ActiveSnapshotAsync(feedId, cancellationToken) is not { } snapshotId)
        {
            return null;
        }

        return await cache.GetOrCreateAsync(
            $"gtfs:{feedId}:{snapshotId}:routes:{routeId}:shapes",
            async cancel =>
            {
                var result = await reader.ListRouteShapesAsync(snapshotId, routeId, cancel)
                    ?? new RouteShapesResult(RouteFound: false, Shapes: []);
                // Materialise nested lists so L2 JSON can round-trip the record.
                return result with { Shapes = result.Shapes.Select(s => s with { Points = s.Points.ToArray() }).ToArray() };
            },
            _entry,
            cancellationToken: cancellationToken);
    }
}

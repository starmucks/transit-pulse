using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Map;

/// <summary>Demo guards for SignalR map subscriptions (ADR-0017).</summary>
public static class MapViewportLimits
{
    /// <summary>Maximum longitude or latitude span in degrees (~55 km).</summary>
    public const double MaxSpanDegrees = 0.5d;

    /// <summary>Maximum vehicles included in one delta frame.</summary>
    public const int MaxVehiclesPerFrame = 500;

    /// <summary>
    /// Covering box for HSL / Helsinki when the client subscribes by <c>routeId</c> only
    /// (GEO needs a box; route filter is applied after the hash join).
    /// </summary>
    public static readonly GeoBoundingBox HelsinkiCoveringBox = new(24.5, 60.1, 25.3, 60.4);

    /// <summary>Validates a subscription and returns the GEO box to query.</summary>
    /// <exception cref="ArgumentException">When the subscription is incomplete or exceeds limits.</exception>
    public static GeoBoundingBox ResolveBox(GeoBoundingBox? bbox, string? routeId)
    {
        var hasBox = bbox is { } b && b.IsWellFormed;
        var hasRoute = !string.IsNullOrWhiteSpace(routeId);
        if (!hasBox && !hasRoute)
        {
            throw new ArgumentException("Subscribe requires a bounding box and/or a routeId.");
        }

        if (hasBox)
        {
            var box = bbox!.Value;
            if (box.LonSpan > MaxSpanDegrees || box.LatSpan > MaxSpanDegrees)
            {
                throw new ArgumentException(
                    $"Bounding box span must be at most {MaxSpanDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture)} degrees in latitude and longitude.");
            }

            return box;
        }

        return HelsinkiCoveringBox;
    }
}

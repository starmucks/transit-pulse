using Microsoft.AspNetCore.SignalR;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Map;

/// <summary>SignalR hub: one viewport/route subscription per connection.</summary>
public sealed class VehicleMapHub(MapSubscriptionRegistry registry, MapDeltaMetrics metrics) : Hub
{
    /// <summary>
    /// Replaces the connection's subscription. Requires <paramref name="feedId"/> and a bbox and/or
    /// <paramref name="routeId"/>. Returns the subscription generation clients must match on
    /// <c>vehicleDelta</c> frames.
    /// </summary>
    public long Subscribe(string feedId, double? west, double? south, double? east, double? north, string? routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);

        GeoBoundingBox? bbox = null;
        if (west is not null || south is not null || east is not null || north is not null)
        {
            if (west is null || south is null || east is null || north is null)
            {
                throw new HubException("Bounding box requires west, south, east and north.");
            }

            bbox = new GeoBoundingBox(west.Value, south.Value, east.Value, north.Value);
        }

        GeoBoundingBox box;
        try
        {
            box = MapViewportLimits.ResolveBox(bbox, routeId);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }

        if (registry.Set(new MapSubscription(Context.ConnectionId, feedId, box, EmptyToNull(routeId))))
        {
            metrics.SubscriptionAdded();
        }

        return registry.GetGeneration(Context.ConnectionId);
    }

    /// <summary>Clears the connection's subscription without disconnecting.</summary>
    public void Unsubscribe()
    {
        if (registry.Remove(Context.ConnectionId))
        {
            metrics.SubscriptionRemoved();
        }
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.Remove(Context.ConnectionId))
        {
            metrics.SubscriptionRemoved();
        }

        return base.OnDisconnectedAsync(exception);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

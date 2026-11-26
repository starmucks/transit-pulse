using System.Diagnostics.Metrics;

namespace TransitPulse.Api.Map;

/// <summary>OpenTelemetry-friendly counters for the map ticker (ADR-0017).</summary>
public sealed class MapDeltaMetrics
{
    /// <summary>Meter name shared with the rest of the API process.</summary>
    public const string MeterName = "TransitPulse.Api.Map";

    private readonly UpDownCounter<int> _subscribers;
    private readonly Counter<long> _deltaVehicles;
    private readonly Counter<long> _truncations;
    private readonly Counter<long> _redisErrors;

    /// <summary>Creates instruments on <paramref name="meterFactory"/>.</summary>
    public MapDeltaMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _subscribers = meter.CreateUpDownCounter<int>("transitpulse_map_subscribers", description: "Active map subscriptions.");
        _deltaVehicles = meter.CreateCounter<long>(
            "transitpulse_map_delta_vehicles",
            description: "Vehicles included in deltas (added + updated + removed).");
        _truncations = meter.CreateCounter<long>(
            "transitpulse_map_viewport_truncations",
            description: "Frames that hit MaxVehiclesPerFrame.");
        _redisErrors = meter.CreateCounter<long>(
            "transitpulse_map_redis_errors",
            description: "Redis failures during a map tick.");
    }

    /// <summary>Records that a subscription was added.</summary>
    public void SubscriptionAdded() => _subscribers.Add(1);

    /// <summary>Records that a subscription was removed.</summary>
    public void SubscriptionRemoved() => _subscribers.Add(-1);

    /// <summary>Records the size of one delta frame.</summary>
    public void DeltaPublished(int added, int updated, int removed) =>
        _deltaVehicles.Add(added + updated + removed);

    /// <summary>Records a truncated viewport result.</summary>
    public void Truncated() => _truncations.Add(1);

    /// <summary>Records a Redis failure on the tick path.</summary>
    public void RedisError() => _redisErrors.Add(1);
}

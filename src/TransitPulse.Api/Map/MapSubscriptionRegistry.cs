using System.Collections.Concurrent;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Map;

/// <summary>One connection's active viewport subscription.</summary>
/// <param name="ConnectionId">SignalR connection id.</param>
/// <param name="FeedId">Feed, e.g. <c>hsl</c>.</param>
/// <param name="Box">GEO box actually queried (may be the Helsinki cover for route-only).</param>
/// <param name="RouteId">Optional route filter.</param>
public sealed record MapSubscription(
    string ConnectionId,
    string FeedId,
    GeoBoundingBox Box,
    string? RouteId);

/// <summary>Thread-safe registry of per-connection map subscriptions and last-sent snapshots.</summary>
public sealed class MapSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of connections with an active subscription.</summary>
    public int Count => _entries.Count;

    /// <summary>True when <paramref name="connectionId"/> already has a subscription.</summary>
    public bool Contains(string connectionId) => _entries.ContainsKey(connectionId);

    /// <summary>
    /// Replaces the subscription for the connection, clears last-sent state, and bumps the generation so an
    /// in-flight tick for the previous viewport cannot overwrite the empty last-sent via
    /// <see cref="ReplaceLastSent"/>.
    /// </summary>
    /// <param name="subscription">Subscription keyed by its <see cref="MapSubscription.ConnectionId"/>.</param>
    /// <returns><see langword="true"/> when this is a new connection id.</returns>
    public bool Set(MapSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var isNew = false;
        _entries.AddOrUpdate(
            subscription.ConnectionId,
            _ =>
            {
                isNew = true;
                return new Entry(
                    subscription,
                    new Dictionary<string, MapVehicle>(StringComparer.Ordinal),
                    Generation: 1);
            },
            (_, existing) => new Entry(
                subscription,
                new Dictionary<string, MapVehicle>(StringComparer.Ordinal),
                existing.Generation + 1));
        return isNew;
    }

    /// <summary>Removes the subscription when the client unsubscribes or disconnects.</summary>
    /// <returns><see langword="true"/> when a subscription was removed.</returns>
    public bool Remove(string connectionId) => _entries.TryRemove(connectionId, out _);

    /// <summary>
    /// Snapshot of all subscriptions for one ticker pass. Each row carries the generation that was current when
    /// the snapshot was taken; <see cref="ReplaceLastSent"/> ignores writes for a superseded generation.
    /// </summary>
    public IReadOnlyList<(
        MapSubscription Subscription,
        IReadOnlyDictionary<string, MapVehicle> LastSent,
        long Generation)> Snapshot()
    {
        var list = new List<(MapSubscription, IReadOnlyDictionary<string, MapVehicle>, long)>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            list.Add((entry.Subscription, entry.LastSent, entry.Generation));
        }

        return list;
    }

    /// <summary>
    /// True when <paramref name="connectionId"/> still holds <paramref name="generation"/> (Subscribe has not
    /// replaced the viewport since the ticker snapped it).
    /// </summary>
    public bool IsCurrent(string connectionId, long generation) =>
        _entries.TryGetValue(connectionId, out var entry) && entry.Generation == generation;

    /// <summary>
    /// Stores the vehicles just sent so the next tick can diff. No-op when the subscription was replaced
    /// (generation bumped) after the ticker took its snapshot. Uses compare-and-swap so a concurrent
    /// <see cref="Set"/> cannot be overwritten with a stale entry.
    /// </summary>
    /// <returns><see langword="true"/> when last-sent was updated.</returns>
    public bool ReplaceLastSent(
        string connectionId,
        long generation,
        IReadOnlyList<MapVehicle> vehicles)
    {
        var map = new Dictionary<string, MapVehicle>(vehicles.Count, StringComparer.Ordinal);
        foreach (var vehicle in vehicles)
        {
            map[vehicle.VehicleId] = vehicle;
        }

        while (_entries.TryGetValue(connectionId, out var entry))
        {
            if (entry.Generation != generation)
            {
                return false;
            }

            var updated = entry with { LastSent = map };
            if (_entries.TryUpdate(connectionId, updated, entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Test/ops helper: current last-sent snapshot, or empty when unknown.</summary>
    public IReadOnlyDictionary<string, MapVehicle> GetLastSent(string connectionId) =>
        _entries.TryGetValue(connectionId, out var entry)
            ? entry.LastSent
            : new Dictionary<string, MapVehicle>(StringComparer.Ordinal);

    /// <summary>Test helper: current generation, or 0 when unknown.</summary>
    public long GetGeneration(string connectionId) =>
        _entries.TryGetValue(connectionId, out var entry) ? entry.Generation : 0;

    private sealed record Entry(
        MapSubscription Subscription,
        IReadOnlyDictionary<string, MapVehicle> LastSent,
        long Generation);
}

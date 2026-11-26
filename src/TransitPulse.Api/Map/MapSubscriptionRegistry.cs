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
        var isNew = !_entries.ContainsKey(subscription.ConnectionId);
        var generation = isNew
            ? 1L
            : _entries[subscription.ConnectionId].Generation + 1;
        _entries[subscription.ConnectionId] = new Entry(
            subscription,
            new Dictionary<string, MapVehicle>(StringComparer.Ordinal),
            generation);
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
    /// Stores the vehicles just sent so the next tick can diff. No-op when the subscription was replaced
    /// (generation bumped) after the ticker took its snapshot.
    /// </summary>
    /// <returns><see langword="true"/> when last-sent was updated.</returns>
    public bool ReplaceLastSent(
        string connectionId,
        long generation,
        IReadOnlyList<MapVehicle> vehicles)
    {
        if (!_entries.TryGetValue(connectionId, out var entry) || entry.Generation != generation)
        {
            return false;
        }

        var map = new Dictionary<string, MapVehicle>(vehicles.Count, StringComparer.Ordinal);
        foreach (var vehicle in vehicles)
        {
            map[vehicle.VehicleId] = vehicle;
        }

        // Re-check after building the map: Subscribe may have raced in.
        if (!_entries.TryGetValue(connectionId, out entry) || entry.Generation != generation)
        {
            return false;
        }

        _entries[connectionId] = entry with { LastSent = map };
        return true;
    }

    /// <summary>Test/ops helper: current last-sent snapshot, or empty when unknown.</summary>
    public IReadOnlyDictionary<string, MapVehicle> GetLastSent(string connectionId) =>
        _entries.TryGetValue(connectionId, out var entry)
            ? entry.LastSent
            : new Dictionary<string, MapVehicle>(StringComparer.Ordinal);

    private sealed record Entry(
        MapSubscription Subscription,
        IReadOnlyDictionary<string, MapVehicle> LastSent,
        long Generation);
}

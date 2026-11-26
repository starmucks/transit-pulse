using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Map;

/// <summary>One SignalR frame of viewport changes.</summary>
/// <param name="Added">Vehicles that entered the subscription since the last tick.</param>
/// <param name="Updated">Vehicles still present whose snapshot changed.</param>
/// <param name="Removed">Vehicle ids that left the subscription (or lost their attribute hash).</param>
/// <param name="Generation">
/// Subscription generation this frame belongs to. Clients ignore frames whose generation does not match
/// the value returned by the last successful <c>Subscribe</c> (closes the SendAsync TOCTOU after pan/zoom).
/// </param>
/// <param name="SentAt">
/// UTC wall clock when the API is about to push this frame (C3 segment ages). Default until the ticker stamps it.
/// </param>
public sealed record MapVehicleDelta(
    IReadOnlyList<MapVehicle> Added,
    IReadOnlyList<MapVehicle> Updated,
    IReadOnlyList<string> Removed,
    long Generation,
    DateTimeOffset SentAt = default);

/// <summary>Computes added/updated/removed against the last frame sent to a connection.</summary>
public static class MapDeltaCalculator
{
    /// <summary>
    /// Diffs <paramref name="current"/> against <paramref name="previous"/> keyed by vehicle id.
    /// A vehicle is <c>updated</c> when any snapshot field differs.
    /// </summary>
    public static MapVehicleDelta Diff(
        IReadOnlyDictionary<string, MapVehicle> previous,
        IReadOnlyList<MapVehicle> current,
        long generation)
    {
        var currentMap = new Dictionary<string, MapVehicle>(current.Count, StringComparer.Ordinal);
        foreach (var vehicle in current)
        {
            currentMap[vehicle.VehicleId] = vehicle;
        }

        var added = new List<MapVehicle>();
        var updated = new List<MapVehicle>();
        foreach (var (id, vehicle) in currentMap)
        {
            if (!previous.TryGetValue(id, out var before))
            {
                added.Add(vehicle);
            }
            else if (before != vehicle)
            {
                updated.Add(vehicle);
            }
        }

        var removed = previous.Keys.Where(id => !currentMap.ContainsKey(id)).ToArray();
        return new MapVehicleDelta(added, updated, removed, generation);
    }
}

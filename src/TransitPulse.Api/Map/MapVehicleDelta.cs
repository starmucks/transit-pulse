using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Map;

/// <summary>One SignalR frame of viewport changes.</summary>
/// <param name="Added">Vehicles that entered the subscription since the last tick.</param>
/// <param name="Updated">Vehicles still present whose snapshot changed.</param>
/// <param name="Removed">Vehicle ids that left the subscription (or lost their attribute hash).</param>
public sealed record MapVehicleDelta(
    IReadOnlyList<MapVehicle> Added,
    IReadOnlyList<MapVehicle> Updated,
    IReadOnlyList<string> Removed);

/// <summary>Computes added/updated/removed against the last frame sent to a connection.</summary>
public static class MapDeltaCalculator
{
    /// <summary>
    /// Diffs <paramref name="current"/> against <paramref name="previous"/> keyed by vehicle id.
    /// A vehicle is <c>updated</c> when any snapshot field differs.
    /// </summary>
    public static MapVehicleDelta Diff(
        IReadOnlyDictionary<string, MapVehicle> previous,
        IReadOnlyList<MapVehicle> current)
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
        return new MapVehicleDelta(added, updated, removed);
    }
}

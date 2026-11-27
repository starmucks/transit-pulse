using System.Globalization;
using StackExchange.Redis;

namespace TransitPulse.Infrastructure.Redis;

/// <summary>GEOSEARCH + attribute-hash join for the SignalR map tick (ADR-0015 / ADR-0017).</summary>
public sealed class VehicleViewportReader(IConnectionMultiplexer connection) : IVehicleViewportReader
{
    // Approximate metres per degree; good enough to size a BYBOX query for demo viewports.
    private const double MetersPerDegreeLat = 110_540d;
    private const double MetersPerDegreeLonAtEquator = 111_320d;

    private readonly IDatabase _database = connection.GetDatabase();

    /// <inheritdoc />
    public async Task<IReadOnlyList<MapVehicle>> ListInBoxAsync(
        string feedId,
        GeoBoundingBox box,
        string? routeId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (!box.IsWellFormed)
        {
            throw new ArgumentException("Bounding box must have west < east and south < north.", nameof(box));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var centerLat = box.CenterLat;
        var widthM = Math.Max(1d, box.LonSpan * MetersPerDegreeLonAtEquator * Math.Cos(DegreesToRadians(centerLat)));
        var heightM = Math.Max(1d, box.LatSpan * MetersPerDegreeLat);

        var geoKey = VehicleGeoStore.GeoKey(feedId);
        var hits = await _database.GeoSearchAsync(
            geoKey,
            box.CenterLon,
            centerLat,
            new GeoSearchBox(widthM, heightM, GeoUnit.Meters),
            count: limit);

        if (hits.Length == 0)
        {
            return [];
        }

        // One pipeline round-trip for all attribute hashes — sequential HashGetAll dominated Redis→send (C3).
        cancellationToken.ThrowIfCancellationRequested();
        var batch = _database.CreateBatch();
        var pending = new (GeoRadiusResult Hit, string VehicleId, Task<HashEntry[]> Hash)[hits.Length];
        for (var i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            var vehicleId = (string)hit.Member!;
            pending[i] = (hit, vehicleId, batch.HashGetAllAsync(VehicleGeoStore.AttributeKey(feedId, vehicleId)));
        }

        batch.Execute();
        await Task.WhenAll(pending.Select(entry => entry.Hash));

        var vehicles = new List<MapVehicle>(hits.Length);
        foreach (var (hit, vehicleId, hashTask) in pending)
        {
            var hash = await hashTask;
            if (hash.Length == 0)
            {
                // GEO member outlived the TTL hash — treat as departed (ADR-0015).
                continue;
            }

            var attrs = ToDictionary(hash);
            if (routeId is not null
                && !string.Equals(Optional(attrs, "route_id"), routeId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryReadVehicle(vehicleId, hit, attrs, out var vehicle))
            {
                continue;
            }

            vehicles.Add(vehicle);
            if (vehicles.Count >= limit)
            {
                break;
            }
        }

        return vehicles;
    }

    private static bool TryReadVehicle(
        string vehicleId,
        GeoRadiusResult hit,
        Dictionary<string, string> attrs,
        out MapVehicle vehicle)
    {
        vehicle = null!;
        var lon = hit.Position?.Longitude
            ?? ParseDouble(Optional(attrs, "lon"));
        var lat = hit.Position?.Latitude
            ?? ParseDouble(Optional(attrs, "lat"));
        if (lon is null || lat is null)
        {
            return false;
        }

        if (!TryParseTimestamp(Optional(attrs, "observed_at"), out var observedAt))
        {
            return false;
        }

        DateTimeOffset? redisWrittenAt = null;
        if (TryParseTimestamp(Optional(attrs, "redis_written_at"), out var writtenAt))
        {
            redisWrittenAt = writtenAt;
        }

        DateTimeOffset? ingestedAt = null;
        if (TryParseTimestamp(EmptyToNull(Optional(attrs, "ingested_at")), out var receivedAt))
        {
            ingestedAt = receivedAt;
        }

        vehicle = new MapVehicle(
            vehicleId,
            lon.Value,
            lat.Value,
            ParseFloat(Optional(attrs, "bearing")),
            EmptyToNull(Optional(attrs, "route_id")),
            EmptyToNull(Optional(attrs, "trip_id")),
            ParseInt(Optional(attrs, "delay_sec")),
            observedAt,
            redisWrittenAt,
            ingestedAt);
        return true;
    }

    private static Dictionary<string, string> ToDictionary(HashEntry[] hash)
    {
        var map = new Dictionary<string, string>(hash.Length, StringComparer.Ordinal);
        foreach (var entry in hash)
        {
            map[(string)entry.Name!] = entry.Value.HasValue ? (string)entry.Value! : "";
        }

        return map;
    }

    private static string? Optional(Dictionary<string, string> attrs, string key) =>
        attrs.TryGetValue(key, out var value) ? value : null;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static float? ParseFloat(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);
}

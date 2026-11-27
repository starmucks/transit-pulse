using System.Globalization;
using StackExchange.Redis;
using TransitPulse.Domain;

namespace TransitPulse.Infrastructure.Redis;

/// <summary>Writes the hot map layer for thinned positions.</summary>
public interface IVehicleGeoStore
{
    /// <summary>Upserts GEO members and attribute hashes for each position.</summary>
    Task UpsertManyAsync(IReadOnlyList<EnrichedPosition> positions, CancellationToken cancellationToken);
}

/// <summary>
/// Hot map layer: Redis GEO per feed plus a short-lived attribute hash per vehicle (ADR-0015).
/// </summary>
public sealed class VehicleGeoStore : IVehicleGeoStore
{
    /// <summary>Default hash TTL when the caller does not pass one.</summary>
    public static readonly TimeSpan DefaultAttributeTtl = TimeSpan.FromSeconds(120);

    private readonly IDatabase _database;
    private readonly TimeSpan _attributeTtl;
    private readonly TimeProvider _time;

    /// <summary>Creates a store over a Redis connection.</summary>
    /// <param name="connection">Shared multiplexer from Aspire or tests.</param>
    /// <param name="attributeTtl">TTL refreshed on each vehicle hash write; must be positive.</param>
    /// <param name="time">Clock for <c>redis_written_at</c> (C3 segment ages); defaults to the system clock.</param>
    public VehicleGeoStore(
        IConnectionMultiplexer connection,
        TimeSpan? attributeTtl = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _attributeTtl = attributeTtl ?? DefaultAttributeTtl;
        if (_attributeTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(attributeTtl), _attributeTtl, "Must be positive.");
        }

        _database = connection.GetDatabase();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>GEO key for one feed's current vehicle set.</summary>
    public static string GeoKey(string feedId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        return $"transitpulse:geo:{feedId}";
    }

    /// <summary>Attribute hash key for one vehicle.</summary>
    public static string AttributeKey(string feedId, string vehicleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        return $"transitpulse:vehicle:{feedId}:{vehicleId}";
    }

    /// <summary>
    /// Upserts GEO members and attribute hashes for each position. Same vehicle member is overwritten (idempotent under
    /// at-least-once delivery).
    /// </summary>
    public async Task UpsertManyAsync(
        IReadOnlyList<EnrichedPosition> positions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var writtenAt = _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var batch = _database.CreateBatch();
        var pending = new List<Task>(positions.Count * 3);

        foreach (var enriched in positions)
        {
            ArgumentNullException.ThrowIfNull(enriched);
            var position = enriched.Position;
            var geoKey = GeoKey(position.FeedId);
            var attrKey = AttributeKey(position.FeedId, position.VehicleId);
            var routeId = enriched.Schedule?.RouteId ?? position.RouteId;
            var tripId = enriched.Schedule?.TripId ?? position.TripId;

            pending.Add(batch.GeoAddAsync(
                geoKey,
                position.Lon,
                position.Lat,
                position.VehicleId));
            pending.Add(batch.HashSetAsync(
                attrKey,
                [
                    new HashEntry("route_id", routeId ?? ""),
                    new HashEntry("trip_id", tripId ?? ""),
                    new HashEntry(
                        "bearing",
                        position.Bearing?.ToString(CultureInfo.InvariantCulture) ?? ""),
                    new HashEntry(
                        "delay_sec",
                        enriched.DelaySec?.ToString(CultureInfo.InvariantCulture) ?? ""),
                    new HashEntry("lon", position.Lon.ToString("G17", CultureInfo.InvariantCulture)),
                    new HashEntry("lat", position.Lat.ToString("G17", CultureInfo.InvariantCulture)),
                    new HashEntry(
                        "observed_at",
                        position.ObservedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                    new HashEntry(
                        "ingested_at",
                        position.IngestedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                    new HashEntry("redis_written_at", writtenAt),
                ]));
            pending.Add(batch.KeyExpireAsync(attrKey, _attributeTtl));
        }

        batch.Execute();
        await Task.WhenAll(pending);
    }
}

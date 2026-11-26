namespace TransitPulse.Infrastructure.Redis;

/// <summary>Reads the current vehicle set inside a geographic box from Redis GEO + attribute hashes.</summary>
public interface IVehicleViewportReader
{
    /// <summary>
    /// Returns vehicles in <paramref name="box"/> for <paramref name="feedId"/>. Members without a live attribute hash
    /// are omitted (ADR-0015). Optional <paramref name="routeId"/> keeps only matching hashes. Results are capped at
    /// <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<MapVehicle>> ListInBoxAsync(
        string feedId,
        GeoBoundingBox box,
        string? routeId,
        int limit,
        CancellationToken cancellationToken);
}

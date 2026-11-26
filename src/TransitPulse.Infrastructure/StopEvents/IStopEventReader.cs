namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>Reads observed stop events for the live-map stop panel.</summary>
public interface IStopEventReader
{
    /// <summary>Lists recent events at one stop, newest first.</summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="stopId">GTFS stop id.</param>
    /// <param name="since">Inclusive lower bound on <c>at</c>.</param>
    /// <param name="limit">Maximum rows (caller clamps).</param>
    /// <param name="cancellationToken">Stops the query.</param>
    Task<IReadOnlyList<ObservedStopEvent>> ListRecentByStopAsync(
        string feedId,
        string stopId,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken);
}

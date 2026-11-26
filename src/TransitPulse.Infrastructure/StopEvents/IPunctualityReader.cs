namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>Reads route punctuality rollups from the continuous aggregate.</summary>
public interface IPunctualityReader
{
    /// <summary>
    /// Returns the day rollup for a route. When the continuous aggregate has no row, returns zeros
    /// (and <see langword="null"/> average) for that day — never <see langword="null"/>.
    /// </summary>
    /// <param name="feedId">Feed, e.g. <c>hsl</c>.</param>
    /// <param name="routeId">GTFS route id.</param>
    /// <param name="day">UTC calendar day.</param>
    /// <param name="cancellationToken">Stops the query.</param>
    Task<RoutePunctualityDay> GetRouteDayAsync(
        string feedId,
        string routeId,
        DateOnly day,
        CancellationToken cancellationToken);
}

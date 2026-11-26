namespace TransitPulse.Infrastructure.StopEvents;

/// <summary>One day of route punctuality from <c>punctuality_by_route_day</c> (or zeros when the CAGG has no row).</summary>
/// <param name="Day">UTC calendar day of the bucket.</param>
/// <param name="FeedId">Feed id.</param>
/// <param name="RouteId">GTFS route id.</param>
/// <param name="Arrivals">Count of <c>arrival</c> events that day.</param>
/// <param name="OnTimeArrivals">Arrivals with <c>delay_sec</c> in the on-time window.</param>
/// <param name="AvgArrivalDelaySec">Mean arrival delay when any arrival has delay; otherwise <see langword="null"/>.</param>
/// <param name="OnTimeEarlySec">Inclusive early bound of the on-time window (seconds; negative = early).</param>
/// <param name="OnTimeLateSec">Inclusive late bound of the on-time window (seconds).</param>
public sealed record RoutePunctualityDay(
    DateOnly Day,
    string FeedId,
    string RouteId,
    long Arrivals,
    long OnTimeArrivals,
    double? AvgArrivalDelaySec,
    int OnTimeEarlySec,
    int OnTimeLateSec);

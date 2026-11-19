using System.Globalization;

namespace TransitPulse.Domain;

/// <summary>The kind of anomaly written to <c>events.anomaly</c>.</summary>
public enum AnomalyKind
{
    /// <summary>Two consecutive vehicles are running much closer than their schedule planned.</summary>
    Bunching,
}

/// <summary>One confirmed episode of two vehicles running too close together.</summary>
/// <param name="FeedId">Source feed, e.g. <c>hsl</c>.</param>
/// <param name="RouteId">GTFS <c>route_id</c> shared by the vehicles.</param>
/// <param name="DirectionId">GTFS <c>direction_id</c>: 0 or 1.</param>
/// <param name="FollowerVehicleId">Vehicle behind in distance along the route.</param>
/// <param name="LeaderVehicleId">The next vehicle ahead.</param>
/// <param name="FollowerTripId">GTFS trip of the follower.</param>
/// <param name="LeaderTripId">GTFS trip of the leader.</param>
/// <param name="FollowerServiceDate">Service day of the follower's trip.</param>
/// <param name="LeaderServiceDate">Service day of the leader's trip.</param>
/// <param name="StartedAt">When the headway first stayed below the threshold; UTC.</param>
/// <param name="ConfirmedAt">When the entry confirmation interval completed; UTC.</param>
/// <param name="ActualHeadwaySec">Scheduled travel time between the vehicles' current positions.</param>
/// <param name="ScheduledHeadwaySec">Planned time between their trips at the follower's position.</param>
public sealed record BunchingEvent(
    string FeedId,
    string RouteId,
    int DirectionId,
    string FollowerVehicleId,
    string LeaderVehicleId,
    string FollowerTripId,
    string LeaderTripId,
    DateOnly FollowerServiceDate,
    DateOnly LeaderServiceDate,
    DateTimeOffset StartedAt,
    DateTimeOffset ConfirmedAt,
    double ActualHeadwaySec,
    double ScheduledHeadwaySec)
{
    /// <summary>The anomaly kind.</summary>
    public AnomalyKind Kind { get; } = AnomalyKind.Bunching;

    /// <summary>Actual headway as a share of the scheduled one.</summary>
    public double HeadwayRatio => ActualHeadwaySec / ScheduledHeadwaySec;

    /// <summary>
    /// Identifies the pair's episode across a replay. The start is included because the same trips may separate and bunch
    /// again; the confirmation time is not, so processing the confirming positions again keeps the id.
    /// </summary>
    public string Id => string.Create(
        CultureInfo.InvariantCulture,
        $"{FeedId}/{RouteId}/{DirectionId}/{FollowerVehicleId}/{FollowerTripId}/{FollowerServiceDate:yyyy-MM-dd}/"
        + $"{LeaderVehicleId}/{LeaderTripId}/{LeaderServiceDate:yyyy-MM-dd}/{StartedAt.UtcDateTime:yyyyMMddTHHmmssfffZ}/bunching");
}

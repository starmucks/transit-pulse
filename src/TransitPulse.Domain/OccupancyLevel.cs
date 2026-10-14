namespace TransitPulse.Domain;

/// <summary>
/// How full a vehicle is, normalized from every source (HSL <c>occu</c>, SIRI <c>Occupancy</c>,
/// GTFS-RT <c>OccupancyStatus</c>). Values follow GTFS-RT; missing data is represented by <c>null</c>.
/// </summary>
public enum OccupancyLevel
{
    /// <summary>No or almost no passengers on board.</summary>
    Empty,

    /// <summary>Many seats are free.</summary>
    ManySeatsAvailable,

    /// <summary>A few seats are free.</summary>
    FewSeatsAvailable,

    /// <summary>Only standing room is left.</summary>
    StandingRoomOnly,

    /// <summary>Standing room only, and it is crowded.</summary>
    CrushedStandingRoomOnly,

    /// <summary>The vehicle is full.</summary>
    Full,

    /// <summary>The vehicle does not take new passengers.</summary>
    NotAcceptingPassengers,
}

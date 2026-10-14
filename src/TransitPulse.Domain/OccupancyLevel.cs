namespace TransitPulse.Domain;

/// <summary>
/// How full a vehicle is, normalized from every source (HSL <c>occu</c>, SIRI <c>Occupancy</c>,
/// GTFS-RT <c>OccupancyStatus</c>). Numeric values match GTFS-RT and are stored in archives,
/// so they must never change; missing data is represented by <c>null</c>.
/// </summary>
public enum OccupancyLevel
{
    /// <summary>No or almost no passengers on board.</summary>
    Empty = 0,

    /// <summary>Many seats are free.</summary>
    ManySeatsAvailable = 1,

    /// <summary>A few seats are free.</summary>
    FewSeatsAvailable = 2,

    /// <summary>Only standing room is left.</summary>
    StandingRoomOnly = 3,

    /// <summary>Standing room only, and it is crowded.</summary>
    CrushedStandingRoomOnly = 4,

    /// <summary>The vehicle is full.</summary>
    Full = 5,

    /// <summary>The vehicle does not take new passengers.</summary>
    NotAcceptingPassengers = 6,
}

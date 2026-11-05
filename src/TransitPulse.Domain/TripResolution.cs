namespace TransitPulse.Domain;

/// <summary>What <see cref="TripSchedule.Find"/> decided about a position's trip.</summary>
public enum TripResolution
{
    /// <summary>Exactly one scheduled run matches.</summary>
    Found,

    /// <summary>The position has no trip: a parked vehicle, or a source record without one.</summary>
    NoDescriptor,

    /// <summary>The position's trip id is not a descriptor of the expected form.</summary>
    Malformed,

    /// <summary>The descriptor's service day is not among the days the schedule holds.</summary>
    DayNotLoaded,

    /// <summary>Neither a route nor a route variant of that name runs on the days the schedule holds.</summary>
    RouteUnknown,

    /// <summary>The route exists, but nothing leaves in that direction at that time on that day.</summary>
    NoDeparture,

    /// <summary>Several runs fit the descriptor, and it has nothing to tell them apart.</summary>
    Ambiguous,
}

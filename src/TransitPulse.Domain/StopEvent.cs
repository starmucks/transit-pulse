namespace TransitPulse.Domain;

/// <summary>What a vehicle did at a stop of its trip.</summary>
public enum StopEventKind
{
    /// <summary>Came to a stop in the stop's zone, having been seen driving into it.</summary>
    Arrival,

    /// <summary>Left the stop's zone after standing in it.</summary>
    Departure,

    /// <summary>Drove through the stop's zone without standing.</summary>
    Passed,
}

/// <summary>A vehicle's arrival at, departure from or pass of one stop of its trip, found by <see cref="StopDetector"/>.</summary>
/// <param name="Kind">What the vehicle did.</param>
/// <param name="Stop">The stop, as the trip's <see cref="StopPattern"/> places it.</param>
/// <param name="At">
/// When: for an arrival, the first moment the vehicle stood; for a departure, when it crossed the end of the zone; for a
/// pass, when it crossed the stop itself. Crossings are interpolated between the two positions around them.
/// </param>
public readonly record struct StopEvent(StopEventKind Kind, StopCall Stop, DateTimeOffset At);

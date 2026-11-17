namespace TransitPulse.Domain;

/// <summary>The scheduled times at one call of a GTFS trip.</summary>
/// <remarks>
/// Missing times are stored as zero and present times as seconds plus one. This keeps every call at three integers instead
/// of two <see cref="Nullable{T}"/> values with their own flags: HSL holds about 1.6 million calls for three service days.
/// </remarks>
public readonly record struct ScheduledStopTime
{
    private readonly int _arrivalPlusOne;
    private readonly int _departurePlusOne;

    /// <summary>Creates one scheduled call.</summary>
    /// <param name="stopSequence">GTFS <c>stop_sequence</c>.</param>
    /// <param name="arrivalSeconds">Arrival after the start of the service day, possibly past 86400.</param>
    /// <param name="departureSeconds">Departure after the start of the service day, possibly past 86400.</param>
    public ScheduledStopTime(int stopSequence, int? arrivalSeconds, int? departureSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stopSequence);
        if (arrivalSeconds is { } arrival)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(arrival);
        }

        if (departureSeconds is { } departure)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(departure);
        }

        StopSequence = stopSequence;
        _arrivalPlusOne = arrivalSeconds is { } a ? checked(a + 1) : 0;
        _departurePlusOne = departureSeconds is { } d ? checked(d + 1) : 0;
    }

    /// <summary>GTFS <c>stop_sequence</c>.</summary>
    public int StopSequence { get; }

    /// <summary>Arrival after the start of the service day, possibly past 86400; <see langword="null"/> when absent.</summary>
    public int? ArrivalSeconds => _arrivalPlusOne == 0 ? null : _arrivalPlusOne - 1;

    /// <summary>Departure after the start of the service day, possibly past 86400; <see langword="null"/> when absent.</summary>
    public int? DepartureSeconds => _departurePlusOne == 0 ? null : _departurePlusOne - 1;
}

/// <summary>
/// The scheduled calls of one GTFS trip, in stop-sequence order. Calendar instances of the same GTFS trip share one
/// timetable, while trips following the same stop pattern keep their own times.
/// </summary>
public sealed class StopTimetable
{
    private readonly ScheduledStopTime[] _calls;

    /// <summary>Builds a timetable from calls in increasing stop-sequence order.</summary>
    /// <param name="calls">The calls of one GTFS trip.</param>
    /// <exception cref="ArgumentException">The calls are not in increasing stop-sequence order.</exception>
    public StopTimetable(ReadOnlySpan<ScheduledStopTime> calls)
    {
        _calls = calls.ToArray();
        for (var i = 1; i < _calls.Length; i++)
        {
            if (_calls[i].StopSequence <= _calls[i - 1].StopSequence)
            {
                throw new ArgumentException(
                    $"Stop sequence {_calls[i].StopSequence} is not after {_calls[i - 1].StopSequence}.", nameof(calls));
            }
        }
    }

    /// <summary>The calls in increasing <see cref="ScheduledStopTime.StopSequence"/> order.</summary>
    public IReadOnlyList<ScheduledStopTime> Calls => _calls;

    /// <summary>Finds the scheduled times of one stop sequence.</summary>
    /// <param name="stopSequence">GTFS <c>stop_sequence</c>.</param>
    /// <param name="call">The call when found.</param>
    /// <returns><see langword="true"/> when the trip calls at that sequence.</returns>
    public bool TryFind(int stopSequence, out ScheduledStopTime call)
    {
        var low = 0;
        var high = _calls.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            call = _calls[middle];
            if (call.StopSequence == stopSequence)
            {
                return true;
            }

            if (call.StopSequence < stopSequence)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        call = default;
        return false;
    }
}

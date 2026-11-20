namespace TransitPulse.Domain;

/// <summary>
/// Finds when one vehicle arrives at, departs from or passes the stops of its trip, from how far along the trip's line
/// each of its positions was placed (<see cref="RouteProgress"/>).
/// </summary>
/// <remarks>
/// <para>
/// A stop's zone is a stretch of the line around it, <see cref="ZoneMetres"/> each way, measured along the line rather
/// than as a circle around the stop: route matching has already told the passes of a loop or a two-way street apart, and
/// a circle would mix them up again. Stops closer than twice that split the stretch between them, so zones never overlap.
/// </para>
/// <para>
/// Standing is judged from the distance gained between two positions, not from a reported speed: every source gives
/// positions, while speed is missing from SIRI and from most MBTA vehicles. So only the last position is kept.
/// </para>
/// <para>
/// An arrival needs the vehicle seen driving into the zone; one first seen standing there (at its first stop, or mid-trip
/// after a restart) only departs. Nothing is reported for a stop crossed between two positions more than
/// <see cref="MaxGapSeconds"/> apart: the time would be a guess, and these times are labels for training.
/// </para>
/// <para>
/// Re-anchoring (ADR-0011) restarts from the new distance: back, so stops are found again (events keep their ids
/// downstream, so the repeats replace the old ones); forward, past stops that were passed unseen.
/// Thresholds are first guesses, to be set by comparing with HSL's own stop events (task 2.7).
/// </para>
/// <para>Not thread-safe: all positions of a vehicle arrive in one partition, read by one loop.</para>
/// </remarks>
public sealed class StopDetector
{
    /// <summary>How far along the line before and after a stop its zone reaches.</summary>
    public const double ZoneMetres = 30;

    /// <summary>Slower than this between two positions, the vehicle stands.</summary>
    public const double StoppedMps = 1.0;

    /// <summary>Two positions closer in time do not judge standing: over a fraction of a second, a repeat shows no move.</summary>
    public const double MinStandingSeconds = 0.5;

    /// <summary>A stop crossed between positions farther apart in time gets no event.</summary>
    public const double MaxGapSeconds = 30;

    private string? _tripId;
    private DateOnly _serviceDate;
    private StopPattern? _pattern;
    private int _next;
    private bool _hasLast;
    private double _lastAlong;
    private DateTimeOffset _lastAt;

    // About the stop at _next: whether the vehicle is in its zone, was seen driving in, has stood, crossed the stop when.
    private bool _inZone;
    private bool _entered;
    private bool _stood;
    private DateTimeOffset? _passedAt;

    internal StopDetectorSnapshot Capture()
    {
        if (_tripId is null)
        {
            return default;
        }

        if (_pattern is null || !_hasLast)
        {
            return new StopDetectorSnapshot(_tripId, _serviceDate, false, 0, 0, default, false, false, false, null);
        }

        return new StopDetectorSnapshot(
            _tripId,
            _serviceDate,
            true,
            _next,
            _lastAlong,
            _lastAt,
            _inZone,
            _entered,
            _stood,
            _passedAt);
    }

    internal static StopDetector Restore(StopDetectorSnapshot snapshot, StopPattern? pattern)
    {
        if (snapshot.TripId is null)
        {
            if (snapshot.ServiceDate != default
                || snapshot.HasPattern
                || snapshot.Next != 0
                || snapshot.LastAlongMetres != 0
                || snapshot.LastAt != default
                || snapshot.InZone
                || snapshot.Entered
                || snapshot.Stood
                || snapshot.PassedAt is not null)
            {
                throw InvalidSnapshot();
            }

            return new StopDetector();
        }

        if (!snapshot.HasPattern)
        {
            if (!ValidUnplaced(snapshot))
            {
                throw InvalidSnapshot();
            }

            return new StopDetector { _tripId = snapshot.TripId, _serviceDate = snapshot.ServiceDate };
        }

        if (pattern is null || snapshot.Next < 0 || snapshot.Next > pattern.Calls.Count)
        {
            throw new ArgumentException("The stop detector snapshot cannot be bound to this pattern.", nameof(pattern));
        }

        if (!ValidPlaced(snapshot, pattern.Calls.Count))
        {
            throw InvalidSnapshot();
        }

        return new StopDetector
        {
            _tripId = snapshot.TripId,
            _serviceDate = snapshot.ServiceDate,
            _pattern = pattern,
            _next = snapshot.Next,
            _hasLast = true,
            _lastAlong = snapshot.LastAlongMetres,
            _lastAt = snapshot.LastAt,
            _inZone = snapshot.InZone,
            _entered = snapshot.Entered,
            _stood = snapshot.Stood,
            _passedAt = snapshot.PassedAt,
        };
    }

    /// <summary>
    /// The stop whose zone the vehicle is approaching or still occupies; the next call after it once the vehicle leaves.
    /// <see langword="null"/> before the vehicle is placed on a trip with stops, or after its last stop.
    /// </summary>
    public StopCall? NextStop => _pattern is not null && _next < _pattern.Calls.Count ? _pattern.Calls[_next] : null;

    /// <summary>Takes the vehicle's next placed position and reports what it did at its trip's stops since the last one.</summary>
    /// <param name="trip">The trip the position belongs to.</param>
    /// <param name="match">Where <see cref="RouteProgress.Advance"/> placed the position; only one on the line moves the vehicle.</param>
    /// <param name="at">When the position was observed; later than the vehicle's previous position.</param>
    /// <param name="events">Receives the events, in the order the vehicle reached them; one move can cross several stops.</param>
    public void Advance(ScheduledTrip trip, RouteMatch match, DateTimeOffset at, ICollection<StopEvent> events)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(events);

        if (!string.Equals(_tripId, trip.TripId, StringComparison.Ordinal) || _serviceDate != trip.ServiceDate)
        {
            _tripId = trip.TripId;
            _serviceDate = trip.ServiceDate;
            _pattern = null;
            _hasLast = false;
        }

        if (trip.Stops is not { } pattern || match.Status != RouteMatchStatus.OnRoute)
        {
            return;
        }

        // A new schedule brings a new pattern for the same run: its stops may differ, so the vehicle is placed among them anew.
        if (!_hasLast || match.Reanchored || !ReferenceEquals(pattern, _pattern))
        {
            _pattern = pattern;
            PlaceAt(match.AlongMetres);
        }
        else
        {
            Step(match.AlongMetres, at, events);
        }

        _hasLast = true;
        _lastAlong = match.AlongMetres;
        _lastAt = at;
    }

    // Skips the stops whose zone ends before the distance, without events.
    private void PlaceAt(double along)
    {
        _next = 0;
        while (_next < _pattern!.Calls.Count && Zone(_next).End < along)
        {
            _next++;
        }

        LeaveZone();
    }

    private void Step(double along, DateTimeOffset at, ICollection<StopEvent> events)
    {
        var seconds = (at - _lastAt).TotalSeconds;
        var seen = seconds <= MaxGapSeconds;
        var calls = _pattern!.Calls;
        while (_next < calls.Count)
        {
            var stop = calls[_next];
            var (start, end) = Zone(_next);
            if (along < start)
            {
                return;
            }

            if (_lastAlong < stop.AlongMetres && along >= stop.AlongMetres)
            {
                _passedAt = seen ? Cross(stop.AlongMetres, along, at) : null;
            }

            if (along <= end)
            {
                if (!_inZone)
                {
                    _inZone = true;
                    _entered = seen && _lastAlong < start;
                }

                if (!_stood && seen && seconds >= MinStandingSeconds && (along - _lastAlong) / seconds < StoppedMps)
                {
                    _stood = true;
                    if (_entered)
                    {
                        events.Add(new StopEvent(StopEventKind.Arrival, stop, _lastAt));
                    }
                }

                return;
            }

            if (seen && _stood)
            {
                events.Add(new StopEvent(StopEventKind.Departure, stop, Cross(end, along, at)));
            }
            else if (seen && _passedAt is { } passedAt)
            {
                events.Add(new StopEvent(StopEventKind.Passed, stop, passedAt));
            }

            // The same move may reach the next stops too: they are judged from the same last position.
            _next++;
            LeaveZone();
        }
    }

    private void LeaveZone()
    {
        _inZone = false;
        _entered = false;
        _stood = false;
        _passedAt = null;
    }

    private (double Start, double End) Zone(int index)
    {
        var calls = _pattern!.Calls;
        var along = calls[index].AlongMetres;
        var start = index > 0 ? Math.Max(along - ZoneMetres, (calls[index - 1].AlongMetres + along) / 2) : along - ZoneMetres;
        var end = index < calls.Count - 1 ? Math.Min(along + ZoneMetres, (along + calls[index + 1].AlongMetres) / 2) : along + ZoneMetres;
        return (start, end);
    }

    // When the vehicle was at the given distance, assuming a steady speed between its last position and this one.
    private DateTimeOffset Cross(double distance, double along, DateTimeOffset at)
    {
        var fraction = along > _lastAlong ? Math.Clamp((distance - _lastAlong) / (along - _lastAlong), 0, 1) : 1;
        return _lastAt + ((at - _lastAt) * fraction);
    }

    private static bool ValidUnplaced(StopDetectorSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.TripId)
        && snapshot.ServiceDate != default
        && snapshot.Next == 0
        && snapshot.LastAlongMetres == 0
        && snapshot.LastAt == default
        && !snapshot.InZone
        && !snapshot.Entered
        && !snapshot.Stood
        && snapshot.PassedAt is null;

    private static bool ValidPlaced(StopDetectorSnapshot snapshot, int stopCount) =>
        !string.IsNullOrWhiteSpace(snapshot.TripId)
        && snapshot.ServiceDate != default
        && double.IsFinite(snapshot.LastAlongMetres)
        && snapshot.LastAlongMetres >= 0
        && snapshot.LastAt != default
        && snapshot.LastAt.Offset == TimeSpan.Zero
        && (!snapshot.Entered || snapshot.InZone)
        && (!snapshot.Stood || snapshot.InZone)
        && (snapshot.PassedAt is not { } passed
            || snapshot.InZone && passed != default && passed.Offset == TimeSpan.Zero && passed <= snapshot.LastAt)
        && (snapshot.Next < stopCount
            || !snapshot.InZone && !snapshot.Entered && !snapshot.Stood && snapshot.PassedAt is null);

    private static ArgumentException InvalidSnapshot() =>
        new("The stop detector snapshot is inconsistent.", "snapshot");
}

internal readonly record struct StopDetectorSnapshot(
    string? TripId,
    DateOnly ServiceDate,
    bool HasPattern,
    int Next,
    double LastAlongMetres,
    DateTimeOffset LastAt,
    bool InZone,
    bool Entered,
    bool Stood,
    DateTimeOffset? PassedAt);

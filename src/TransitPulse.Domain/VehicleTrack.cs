namespace TransitPulse.Domain;

/// <summary>
/// One vehicle's track, as far as judging its next position needs: whether the position is newer than the last one, and
/// whether the vehicle could have got there from its last known place.
/// </summary>
/// <remarks>
/// <para>
/// Speed is measured from the time a place was first reported. Some vehicles repeat known coordinates about once a
/// second between real fixes (HSL metro), so comparing consecutive messages would show a normal move as a burst of speed.
/// </para>
/// <para>
/// A jump is not dropped for good. The point is set aside: if the next position is plausible from the old place, the jump
/// was a one-point glitch; if it is plausible from the point set aside, the vehicle really is there, and the track moves.
/// Most measured jumps are of the second kind, so without this a vehicle would stay rejected until time made the distance
/// plausible. Both cases lose one point. Thresholds are measured on three archive hours (<c>docs/benchmarks.md</c>,
/// "Plausibility filter").
/// </para>
/// <para>Not thread-safe: all positions of a vehicle arrive in one partition, read by one loop.</para>
/// </remarks>
public sealed class VehicleTrack
{
    /// <summary>A faster move between two places is a jump; measured HSL train speed is at most 44 m/s at p99.</summary>
    public const double MaxSpeedMps = 60;

    /// <summary>A shorter move is never a jump: over a fraction of a second, GPS noise alone looks impossibly fast.</summary>
    public const double MinJumpMetres = 200;

    private DateTimeOffset _lastObservedAt;
    private Fix? _place;
    private Fix? _setAside;

    /// <summary>Judges the vehicle's next position and updates the track with it.</summary>
    /// <param name="position">The vehicle's next position.</param>
    /// <returns>Whether the position may be processed, or why not.</returns>
    public PositionCheck Check(VehiclePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        var next = new Fix(position.Lat, position.Lon, position.ObservedAt);
        if (_place is not { } place)
        {
            _place = next;
            _lastObservedAt = next.At;
            return PositionCheck.Accepted;
        }

        // Also keeps every time difference below positive: a place's time is never later than the last position's.
        if (next.At <= _lastObservedAt)
        {
            return PositionCheck.OutOfOrder;
        }

        _lastObservedAt = next.At;

        if (!IsJump(place, next))
        {
            // Same coordinates keep the time they were first reported at, see the remarks.
            _place = next.IsAt(place) ? place : next;
            _setAside = null;
            return PositionCheck.Accepted;
        }

        if (_setAside is { } setAside && !IsJump(setAside, next))
        {
            _place = next.IsAt(setAside) ? setAside : next;
            _setAside = null;
            return PositionCheck.Accepted;
        }

        _setAside = next;
        return PositionCheck.ImpossibleJump;
    }

    private static bool IsJump(Fix from, Fix to)
    {
        var metres = GeoDistance.Metres(from.Lat, from.Lon, to.Lat, to.Lon);
        return metres > MinJumpMetres && metres > MaxSpeedMps * (to.At - from.At).TotalSeconds;
    }

    private readonly record struct Fix(double Lat, double Lon, DateTimeOffset At)
    {
        // Exact on purpose: a repeat carries the same numbers, and a nearly equal fix is a new one.
        public bool IsAt(Fix other) => Lat == other.Lat && Lon == other.Lon;
    }
}

namespace TransitPulse.Domain;

/// <summary>
/// How far one vehicle has got along its trip's line: places each position on the line, near where the vehicle was
/// before, so that loops and streets driven both ways resolve to the right pass.
/// </summary>
/// <remarks>
/// <para>
/// The first position of a trip is placed on the first pass of the line near it. Each next one is searched in a window from a little behind
/// the last distance to as far as the vehicle could have driven since its last position on the line. Measuring from that
/// position, not the last one received, lets the window grow during a detour, so the vehicle is picked up where it rejoins.
/// </para>
/// <para>
/// The distance never decreases on a trip: a small step back is GPS noise, and stop events must not see a stop twice.
/// Thresholds and their measured effect: ADR-0011; they are checked again against stop events (task 2.7).
/// </para>
/// <para>
/// A trip's first position may still land on the wrong pass: a vehicle first seen mid-trip (after a restart) on a street
/// the line drives both ways. Every next position then misses the window while lying on the line. After
/// <see cref="ReanchorAfter"/> such positions in a row the vehicle is placed anew along the whole line. One miss is not
/// enough: a single bad fix near another pass of the line must not move the vehicle there.
/// </para>
/// <para>
/// The row must also have moved forward along the line by more than GPS noise. A vehicle driving back along it is not on
/// its trip (a descriptor left over from the previous run, measured on HSL trains for up to an hour): following it would
/// take the distance back past stops already served, so it stays outside the window instead.
/// </para>
/// <para>Not thread-safe: all positions of a vehicle arrive in one partition, read by one loop.</para>
/// </remarks>
public sealed class RouteProgress
{
    /// <summary>A position farther from the line is off route.</summary>
    public const double OffRouteMetres = 50;

    /// <summary>How far behind the last distance the window starts: room for GPS noise without letting the vehicle go back.</summary>
    public const double NoiseMetres = 30;

    /// <summary>
    /// Added to the farthest drivable distance. Covers HSL metro, whose position stays put for seconds and then jumps ahead
    /// (with 30 m, 2 % of metro positions fell outside the window); lets a single fix up to this far ahead through.
    /// </summary>
    public const double MarginMetres = 100;

    /// <summary>How many positions in a row on the line but outside the window place the vehicle anew.</summary>
    public const int ReanchorAfter = 3;

    private string? _tripId;
    private DateOnly _serviceDate;
    private bool _onLine;
    private double _along;
    private DateTimeOffset _at;
    private int _misses;
    private double _rearmost;

    /// <summary>Places the vehicle's next position on its trip's line and updates the progress.</summary>
    /// <param name="trip">The trip the position belongs to.</param>
    /// <param name="lat">Latitude of the position, degrees.</param>
    /// <param name="lon">Longitude of the position, degrees.</param>
    /// <param name="at">When the position was observed; later than the vehicle's previous position.</param>
    /// <returns>Whether the position is on the line, and how far along it the vehicle has got.</returns>
    public RouteMatch Advance(ScheduledTrip trip, double lat, double lon, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(trip);

        if (trip.Shape is not { } shape)
        {
            _tripId = null;
            return new RouteMatch(RouteMatchStatus.NoShape, 0, 0);
        }

        // Compared by id, not by instance: the schedule is read again every day and on every new snapshot, bringing new
        // instances of the same runs. The progress is kept and searched for on the new shape: starting over would put a
        // vehicle mid-trip back on the line's first pass. If the new shape differs, the window misses and re-anchoring follows.
        if (!string.Equals(_tripId, trip.TripId, StringComparison.Ordinal) || _serviceDate != trip.ServiceDate)
        {
            _tripId = trip.TripId;
            _serviceDate = trip.ServiceDate;
            _onLine = false;
            _along = 0;
            _misses = 0;
        }

        ShapeProjection projection;
        if (_onLine)
        {
            var seconds = Math.Max(0, (at - _at).TotalSeconds);
            var reach = (VehicleTrack.MaxSpeedMps * seconds) + MarginMetres;
            projection = shape.Project(lat, lon, _along - NoiseMetres, _along + reach);
        }
        else
        {
            // A trip almost always starts where its line starts: at a terminal the line passes twice, the nearest pass
            // is often the wrong one. A vehicle first seen further on misses the window and is moved forward.
            projection = shape.ProjectFirstPass(lat, lon, OffRouteMetres);
        }

        if (projection.OffsetMetres > OffRouteMetres)
        {
            return _onLine ? Missed(shape, lat, lon, at, projection.OffsetMetres) : new RouteMatch(RouteMatchStatus.OffRoute, _along, projection.OffsetMetres);
        }

        _misses = 0;
        _along = _onLine ? Math.Max(_along, projection.AlongMetres) : projection.AlongMetres;
        _at = at;
        _onLine = true;
        return new RouteMatch(RouteMatchStatus.OnRoute, _along, projection.OffsetMetres);
    }

    // A position off the line in the window: a detour or a bad fix if it is off the whole line too, otherwise a sign that the
    // vehicle was placed on the wrong pass.
    private RouteMatch Missed(RouteShape shape, double lat, double lon, DateTimeOffset at, double windowOffset)
    {
        var whole = shape.Project(lat, lon);
        if (whole.OffsetMetres > OffRouteMetres)
        {
            _misses = 0;
            return new RouteMatch(RouteMatchStatus.OffRoute, _along, windowOffset);
        }

        // Direction is judged over the row, from its rearmost point: at one position a second, a train backing away moves
        // less per step than GPS noise. A vehicle standing still waits here until it moves off.
        _rearmost = _misses++ == 0 ? whole.AlongMetres : Math.Min(_rearmost, whole.AlongMetres);
        // The vehicle was seen on the line, so the window does not grow as it does during a detour: growing, it would reach
        // a later pass near a vehicle standing still and move it there without re-anchoring.
        _at = at;
        if (_misses < ReanchorAfter || whole.AlongMetres - _rearmost <= NoiseMetres)
        {
            return new RouteMatch(RouteMatchStatus.OutsideWindow, _along, whole.OffsetMetres);
        }

        _misses = 0;
        var jump = whole.AlongMetres - _along;
        _along = whole.AlongMetres;
        return new RouteMatch(RouteMatchStatus.OnRoute, _along, whole.OffsetMetres, ReanchoredByMetres: jump);
    }
}

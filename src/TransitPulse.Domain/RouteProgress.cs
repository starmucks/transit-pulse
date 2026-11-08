namespace TransitPulse.Domain;

/// <summary>
/// How far one vehicle has got along its trip's line: places each position on the line, near where the vehicle was
/// before, so that loops and streets driven both ways resolve to the right pass.
/// </summary>
/// <remarks>
/// <para>
/// The first position of a trip is searched along the whole line. Each next one is searched in a window from a little behind
/// the last distance to as far as the vehicle could have driven since its last position on the line. Measuring from that
/// position, not the last one received, lets the window grow during a detour, so the vehicle is picked up where it rejoins.
/// </para>
/// <para>
/// The distance never decreases on a trip: a small step back is GPS noise, and stop events must not see a stop twice.
/// Thresholds are first guesses, to be set by the measured offsets of an archive hour (task 2.6).
/// </para>
/// <para>
/// Known limit: a trip's first position on a street driven both ways may land on the wrong pass. A vehicle is first seen
/// mid-trip only after a restart of the Processor, until state snapshots (task 2.10).
/// </para>
/// <para>Not thread-safe: all positions of a vehicle arrive in one partition, read by one loop.</para>
/// </remarks>
public sealed class RouteProgress
{
    /// <summary>A position farther from the line is off route.</summary>
    public const double OffRouteMetres = 50;

    /// <summary>How far behind the last distance the window starts: room for GPS noise without letting the vehicle go back.</summary>
    public const double NoiseMetres = 30;

    /// <summary>Added to the farthest drivable distance, so that a vehicle standing still still has a window ahead.</summary>
    public const double MarginMetres = 100;

    private string? _tripId;
    private DateOnly _serviceDate;
    private RouteShape? _shape;
    private bool _onLine;
    private double _along;
    private DateTimeOffset _at;

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

        // Compared by id, not by instance: a reloaded schedule brings new instances of the same runs. A new shape instance
        // comes only with a new snapshot, whose distances may differ, so the search starts over.
        if (!string.Equals(_tripId, trip.TripId, StringComparison.Ordinal) || _serviceDate != trip.ServiceDate || _shape != shape)
        {
            _tripId = trip.TripId;
            _serviceDate = trip.ServiceDate;
            _shape = shape;
            _onLine = false;
            _along = 0;
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
            projection = shape.Project(lat, lon);
        }

        if (projection.OffsetMetres > OffRouteMetres)
        {
            return new RouteMatch(RouteMatchStatus.OffRoute, _along, projection.OffsetMetres);
        }

        _along = _onLine ? Math.Max(_along, projection.AlongMetres) : projection.AlongMetres;
        _at = at;
        _onLine = true;
        return new RouteMatch(RouteMatchStatus.OnRoute, _along, projection.OffsetMetres);
    }
}

namespace TransitPulse.Domain;

/// <summary>
/// The line a trip follows (a GTFS shape), measured in metres from its first point. Projects a position onto the line to
/// tell how far along the route the vehicle is and how far off the line it is.
/// </summary>
/// <remarks>
/// <para>
/// Lengths are computed here rather than taken from <c>shape_dist_traveled</c>: that field is optional and its unit differs
/// between feeds, while this gives every city the same scale.
/// </para>
/// <para>
/// The projection works in a flat plane around the position (longitude scaled by the cosine of its latitude), like
/// <see cref="GeoDistance.Metres"/>. Immutable, so trips sharing a shape may share one instance across threads.
/// </para>
/// </remarks>
public sealed class RouteShape
{
    private readonly double[] _lats;
    private readonly double[] _lons;

    // Distance from the first point to each point; never decreasing, which lets a window stop the search early.
    private readonly double[] _along;

    /// <summary>Creates a shape from its points in order.</summary>
    /// <param name="lats">Latitudes of the points, degrees.</param>
    /// <param name="lons">Longitudes of the points, degrees; as many as latitudes.</param>
    /// <exception cref="ArgumentException">The counts differ, or there are fewer than two points.</exception>
    public RouteShape(ReadOnlySpan<double> lats, ReadOnlySpan<double> lons)
    {
        if (lats.Length != lons.Length)
        {
            throw new ArgumentException("A shape needs as many longitudes as latitudes.", nameof(lons));
        }

        if (lats.Length < 2)
        {
            throw new ArgumentException("A shape needs at least two points.", nameof(lats));
        }

        _lats = lats.ToArray();
        _lons = lons.ToArray();
        _along = new double[lats.Length];
        for (var i = 1; i < _along.Length; i++)
        {
            _along[i] = _along[i - 1] + GeoDistance.Metres(_lats[i - 1], _lons[i - 1], _lats[i], _lons[i]);
        }
    }

    /// <summary>Length of the whole shape in metres.</summary>
    public double LengthMetres => _along[^1];

    /// <summary>
    /// Finds the point of the line nearest to a position, considering only the part of the line between two distances
    /// along it. Of equally near points, the one earlier along the line wins.
    /// </summary>
    /// <param name="lat">Latitude of the position, degrees.</param>
    /// <param name="lon">Longitude of the position, degrees.</param>
    /// <param name="fromMetres">Start of the part to search; clamped to the shape. A window past the end gives the last point.</param>
    /// <param name="toMetres">End of the part to search; clamped to the shape.</param>
    /// <returns>The nearest point's distance along the line, the position's distance from it, and its segment.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toMetres"/> is less than <paramref name="fromMetres"/>.</exception>
    public ShapeProjection Project(double lat, double lon, double fromMetres = 0, double toMetres = double.PositiveInfinity)
    {
        if (toMetres < fromMetres)
        {
            throw new ArgumentOutOfRangeException(nameof(toMetres), toMetres, "The window ends before it starts.");
        }

        var from = Math.Clamp(fromMetres, 0, LengthMetres);
        var to = Math.Clamp(toMetres, from, LengthMetres);
        var xScale = GeoDistance.MetresPerDegree * Math.Cos(double.DegreesToRadians(lat));

        var bestSquared = double.PositiveInfinity;
        var bestAlong = 0.0;
        var bestSegment = 0;
        for (var i = 0; i < _along.Length - 1; i++)
        {
            var start = _along[i];
            var end = _along[i + 1];
            if (end < from)
            {
                continue;
            }

            if (start > to)
            {
                break;
            }

            // The position is the origin; the segment runs from (ax, ay) by (dx, dy).
            var ax = (_lons[i] - lon) * xScale;
            var ay = (_lats[i] - lat) * GeoDistance.MetresPerDegree;
            var dx = (_lons[i + 1] - _lons[i]) * xScale;
            var dy = (_lats[i + 1] - _lats[i]) * GeoDistance.MetresPerDegree;

            // A repeated point makes a segment of zero length: its only point is its start.
            var t = 0.0;
            var length = end - start;
            if (length > 0)
            {
                var nearest = -((ax * dx) + (ay * dy)) / ((dx * dx) + (dy * dy));
                var low = Math.Max(0, (from - start) / length);
                var high = Math.Min(1, (to - start) / length);
                t = Math.Max(low, Math.Min(high, nearest));
            }

            var px = ax + (t * dx);
            var py = ay + (t * dy);
            var squared = (px * px) + (py * py);
            if (squared < bestSquared)
            {
                bestSquared = squared;
                bestAlong = start + (t * length);
                bestSegment = i;
            }
        }

        return new ShapeProjection(bestAlong, Math.Sqrt(bestSquared), bestSegment);
    }
}

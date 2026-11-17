namespace TransitPulse.Domain;

/// <summary>A stop a trip calls at, as the schedule gives it.</summary>
/// <param name="StopId">GTFS <c>stop_id</c>.</param>
/// <param name="Sequence">GTFS <c>stop_sequence</c>: increases along the trip, not necessarily by one.</param>
/// <param name="Lat">Latitude of the stop, degrees.</param>
/// <param name="Lon">Longitude of the stop, degrees.</param>
public readonly record struct StopPoint(string StopId, int Sequence, double Lat, double Lon);

/// <summary>A stop a trip calls at, placed on the trip's line.</summary>
/// <param name="StopId">GTFS <c>stop_id</c>.</param>
/// <param name="Sequence">GTFS <c>stop_sequence</c>.</param>
/// <param name="AlongMetres">Distance along the line, on the scale of <see cref="RouteShape"/>; never less than the previous stop's.</param>
/// <param name="OffsetMetres">
/// Distance from the stop to that point of the line. Above <see cref="StopPattern.NearLineMetres"/> the placement is a
/// guess: the line never comes that near after the previous stop.
/// </param>
public readonly record struct StopCall(string StopId, int Sequence, double AlongMetres, double OffsetMetres);

/// <summary>
/// The stops of a trip in order, each placed on the trip's line: where along the line a vehicle reaches it. Trips that
/// follow the same shape through the same stops share one pattern (HSL: 1 203 patterns for 380 543 trips).
/// </summary>
/// <remarks>
/// <para>
/// Stops are projected onto the shape rather than read from <c>shape_dist_traveled</c>: HSL gives that field in kilometres,
/// Entur in metres, MBTA not at all, and positions are placed on the shape's own scale (<see cref="RouteShape"/>).
/// </para>
/// <para>
/// Each stop is placed on the first pass of the line that comes within <see cref="NearLineMetres"/> after the previous stop,
/// not on the nearest point of the whole line: a line that passes the same place twice (a loop, a street driven both ways)
/// would otherwise put a later stop before an earlier one. Immutable, so trips may share one instance across threads.
/// </para>
/// </remarks>
public sealed class StopPattern
{
    /// <summary>How near the line must come to a stop for its placement to be trusted.</summary>
    public const double NearLineMetres = 50;

    private readonly StopCall[] _calls;

    /// <summary>Places the stops of a trip on its line.</summary>
    /// <param name="shape">The line the trip follows.</param>
    /// <param name="stops">The trip's stops in the order it calls at them.</param>
    /// <exception cref="ArgumentException">The stops are not in increasing <see cref="StopPoint.Sequence"/> order.</exception>
    public StopPattern(RouteShape shape, ReadOnlySpan<StopPoint> stops)
    {
        ArgumentNullException.ThrowIfNull(shape);

        _calls = new StopCall[stops.Length];
        var from = 0.0;
        for (var i = 0; i < stops.Length; i++)
        {
            var stop = stops[i];
            if (i > 0 && stop.Sequence <= stops[i - 1].Sequence)
            {
                throw new ArgumentException(
                    $"Stop {stop.StopId} has sequence {stop.Sequence}, not after {stops[i - 1].Sequence}.", nameof(stops));
            }

            var placed = shape.ProjectFirstPass(stop.Lat, stop.Lon, NearLineMetres, from);
            _calls[i] = new StopCall(stop.StopId, stop.Sequence, placed.AlongMetres, placed.OffsetMetres);
            from = placed.AlongMetres;
        }
    }

    /// <summary>The stops in the order the trip calls at them.</summary>
    public IReadOnlyList<StopCall> Calls => _calls;
}

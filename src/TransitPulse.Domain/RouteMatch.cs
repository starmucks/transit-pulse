namespace TransitPulse.Domain;

/// <summary>Where <see cref="RouteProgress.Advance"/> placed a position on its trip's line.</summary>
/// <param name="Status">Whether the position is on the line.</param>
/// <param name="AlongMetres">
/// How far along the line the vehicle has got, never less than before on the same trip. For a position off the line, the
/// last distance on it, or 0 before the trip's first position on the line.
/// </param>
/// <param name="OffsetMetres">Distance from the position to the line where it was searched; 0 when there is no shape.</param>
public readonly record struct RouteMatch(RouteMatchStatus Status, double AlongMetres, double OffsetMetres);

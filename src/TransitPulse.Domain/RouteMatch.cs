namespace TransitPulse.Domain;

/// <summary>Where <see cref="RouteProgress.Advance"/> placed a position on its trip's line.</summary>
/// <param name="Status">Whether the position is on the line.</param>
/// <param name="AlongMetres">
/// How far along the line the vehicle has got, never less than before on the same trip unless <paramref name="Reanchored"/>.
/// For a position not placed, the last distance on the line, or 0 before the trip's first position on the line.
/// </param>
/// <param name="OffsetMetres">
/// Distance from the position to the line where it was searched: near the vehicle's progress, or along the whole line for
/// the trip's first position, <see cref="RouteMatchStatus.OutsideWindow"/> and a new placement; 0 when there is no shape.
/// </param>
/// <param name="Reanchored">
/// The earlier placement was wrong and the vehicle was placed anew, so the distance may have gone back: anything that
/// follows the distance (stop events) has to start over for this trip.
/// </param>
public readonly record struct RouteMatch(RouteMatchStatus Status, double AlongMetres, double OffsetMetres, bool Reanchored = false);

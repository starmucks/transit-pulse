namespace TransitPulse.Domain;

/// <summary>Where <see cref="RouteProgress.Advance"/> placed a position on its trip's line.</summary>
/// <param name="Status">Whether the position is on the line.</param>
/// <param name="AlongMetres">
/// How far along the line the vehicle has got, never less than before on the same trip unless <see cref="Reanchored"/>.
/// For a position not placed, the last distance on the line, or 0 before the trip's first position on the line.
/// </param>
/// <param name="OffsetMetres">
/// Distance from the position to the line where it was searched: in the window around the vehicle's progress, on the
/// line's first pass near it for the trip's first position, or along the whole line for
/// <see cref="RouteMatchStatus.OutsideWindow"/> and a new placement; 0 when there is no shape.
/// </param>
/// <param name="ReanchoredByMetres">
/// How far the vehicle was moved along the line when it was placed anew, negative when back; 0 when it was not. A placement
/// anew always moves it, since the new pass lies outside the window around the old distance.
/// </param>
public readonly record struct RouteMatch(RouteMatchStatus Status, double AlongMetres, double OffsetMetres, double ReanchoredByMetres = 0)
{
    /// <summary>
    /// The earlier placement was wrong and the vehicle was placed anew, so the distance may have gone back: anything that
    /// follows the distance (stop events) has to start over for this trip.
    /// </summary>
    public bool Reanchored => ReanchoredByMetres != 0;
}

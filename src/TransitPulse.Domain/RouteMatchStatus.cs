namespace TransitPulse.Domain;

/// <summary>Whether a position could be placed on its trip's line.</summary>
public enum RouteMatchStatus
{
    /// <summary>Close enough to the line: the distance along it is known.</summary>
    OnRoute,

    /// <summary>Too far from the line, by GPS error or a detour: the distance along it stays as it was.</summary>
    OffRoute,

    /// <summary>The trip has no shape to place the position on.</summary>
    NoShape,

    /// <summary>
    /// On the line, but too far from where the vehicle was before: the distance along it stays as it was. Several such
    /// positions in a row mean the earlier placement was wrong, and the vehicle is placed anew.
    /// </summary>
    OutsideWindow,
}

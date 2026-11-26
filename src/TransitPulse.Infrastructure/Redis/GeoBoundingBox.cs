namespace TransitPulse.Infrastructure.Redis;

/// <summary>WGS 84 bounding box for a Redis GEO viewport query (west/south/east/north).</summary>
/// <param name="West">Minimum longitude.</param>
/// <param name="South">Minimum latitude.</param>
/// <param name="East">Maximum longitude.</param>
/// <param name="North">Maximum latitude.</param>
public readonly record struct GeoBoundingBox(double West, double South, double East, double North)
{
    /// <summary>Longitude of the box centre.</summary>
    public double CenterLon => (West + East) / 2d;

    /// <summary>Latitude of the box centre.</summary>
    public double CenterLat => (South + North) / 2d;

    /// <summary>Longitude span in degrees (non-negative when the box is well-formed).</summary>
    public double LonSpan => East - West;

    /// <summary>Latitude span in degrees (non-negative when the box is well-formed).</summary>
    public double LatSpan => North - South;

    /// <summary>True when west &lt; east, south &lt; north, and coordinates are finite.</summary>
    public bool IsWellFormed =>
        double.IsFinite(West) && double.IsFinite(South) && double.IsFinite(East) && double.IsFinite(North)
        && West < East && South < North;
}

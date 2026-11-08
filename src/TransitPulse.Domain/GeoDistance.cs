namespace TransitPulse.Domain;

/// <summary>Distances between WGS 84 coordinates.</summary>
public static class GeoDistance
{
    private const double EarthRadiusMetres = 6_371_000;

    /// <summary>Length of one degree of latitude, and of longitude on the equator, in the same approximation.</summary>
    internal const double MetresPerDegree = EarthRadiusMetres * Math.PI / 180;

    /// <summary>
    /// Distance in metres by the equirectangular approximation: longitude is scaled by the cosine of the middle
    /// latitude, and the two offsets are treated as a flat right triangle. Accurate enough at city scale and cheap at
    /// stream rate. It does not handle the 180° meridian, which no served city comes near.
    /// </summary>
    /// <param name="lat1">Latitude of the first point, degrees.</param>
    /// <param name="lon1">Longitude of the first point, degrees.</param>
    /// <param name="lat2">Latitude of the second point, degrees.</param>
    /// <param name="lon2">Longitude of the second point, degrees.</param>
    /// <returns>The distance in metres, never negative.</returns>
    public static double Metres(double lat1, double lon1, double lat2, double lon2)
    {
        var x = double.DegreesToRadians(lon2 - lon1) * Math.Cos(double.DegreesToRadians((lat1 + lat2) / 2));
        var y = double.DegreesToRadians(lat2 - lat1);
        return EarthRadiusMetres * Math.Sqrt((x * x) + (y * y));
    }
}

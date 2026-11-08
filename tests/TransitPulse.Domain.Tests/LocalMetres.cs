namespace TransitPulse.Domain.Tests;

/// <summary>Test geometry in metres east and north of a point at Helsinki's latitude, where answers are easy to work out.</summary>
internal static class LocalMetres
{
    private const double OriginLat = 60;
    private const double OriginLon = 25;
    private const double MetresPerDegreeLat = 111_195;
    private const double MetresPerDegreeLon = MetresPerDegreeLat / 2; // cos 60° = 1/2

    public static double Lat(double north) => OriginLat + (north / MetresPerDegreeLat);

    public static double Lon(double east) => OriginLon + (east / MetresPerDegreeLon);

    public static RouteShape Shape(params (double East, double North)[] points) =>
        new([.. points.Select(p => Lat(p.North))], [.. points.Select(p => Lon(p.East))]);
}

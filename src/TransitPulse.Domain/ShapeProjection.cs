namespace TransitPulse.Domain;

/// <summary>Where a position lies relative to a <see cref="RouteShape"/>.</summary>
/// <param name="AlongMetres">Distance along the shape from its first point to the nearest point of the line.</param>
/// <param name="OffsetMetres">Distance from the position to that nearest point: how far the vehicle is off the line.</param>
/// <param name="Segment">Index of the segment the nearest point lies on: 0 is between the first and the second point.</param>
public readonly record struct ShapeProjection(double AlongMetres, double OffsetMetres, int Segment);

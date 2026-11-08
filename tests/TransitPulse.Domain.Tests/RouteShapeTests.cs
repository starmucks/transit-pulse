using static TransitPulse.Domain.Tests.LocalMetres;

namespace TransitPulse.Domain.Tests;

public sealed class RouteShapeTests
{
    [Fact]
    public void A_point_beside_a_straight_line_lies_across_from_its_foot()
    {
        var shape = Shape((0, 0), (1000, 0));

        var projection = Project(shape, 400, 30);

        Assert.Equal(400, projection.AlongMetres, tolerance: 0.5);
        Assert.Equal(30, projection.OffsetMetres, tolerance: 0.5);
        Assert.Equal(0, projection.Segment);
    }

    [Fact]
    public void Distance_goes_on_past_a_corner()
    {
        var shape = Shape((0, 0), (1000, 0), (1000, 500));

        var projection = Project(shape, 1020, 200);

        Assert.Equal(1200, projection.AlongMetres, tolerance: 0.5);
        Assert.Equal(20, projection.OffsetMetres, tolerance: 0.5);
        Assert.Equal(1, projection.Segment);
    }

    [Fact]
    public void A_point_before_the_start_lies_at_the_first_point()
    {
        var projection = Project(Shape((0, 0), (1000, 0)), -50, 0);

        Assert.Equal(0, projection.AlongMetres);
        Assert.Equal(50, projection.OffsetMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_point_past_the_end_lies_at_the_last_point()
    {
        var projection = Project(Shape((0, 0), (1000, 0)), 1100, 0);

        Assert.Equal(1000, projection.AlongMetres, tolerance: 0.5);
        Assert.Equal(100, projection.OffsetMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_repeated_point_adds_no_length_and_breaks_nothing()
    {
        var shape = Shape((0, 0), (500, 0), (500, 0), (1000, 0));

        var projection = Project(shape, 700, 10);

        Assert.Equal(1000, shape.LengthMetres, tolerance: 0.5);
        Assert.Equal(700, projection.AlongMetres, tolerance: 0.5);
        Assert.Equal(2, projection.Segment);
    }

    [Fact]
    public void A_window_picks_the_way_back_along_a_street_driven_both_ways()
    {
        // Out along one side of a street and back along the other, 20 m apart; the point is nearer the way out.
        var shape = Shape((0, 0), (1000, 0), (1000, 20), (0, 20));

        var anywhere = Project(shape, 300, 8);
        var onTheWayBack = Project(shape, 300, 8, fromMetres: 1100);

        Assert.Equal(300, anywhere.AlongMetres, tolerance: 0.5);
        Assert.Equal(1720, onTheWayBack.AlongMetres, tolerance: 0.5);
        Assert.Equal(12, onTheWayBack.OffsetMetres, tolerance: 0.5);
    }

    [Fact]
    public void The_first_pass_wins_over_a_nearer_later_pass()
    {
        // 18 m from the way out, 2 m from the way back.
        var shape = Shape((0, 0), (1000, 0), (1000, 20), (0, 20));

        var first = shape.ProjectFirstPass(Lat(18), Lon(300), 50);
        var tooFar = shape.ProjectFirstPass(Lat(18), Lon(300), 10);

        Assert.Equal(300, first.AlongMetres, tolerance: 0.5);
        Assert.Equal(18, first.OffsetMetres, tolerance: 0.5);
        Assert.Equal(1720, tooFar.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void Without_a_pass_that_near_the_first_pass_is_the_nearest_point()
    {
        var shape = Shape((0, 0), (1000, 0));

        var projection = shape.ProjectFirstPass(Lat(300), Lon(400), 50);

        Assert.Equal(new ShapeProjection(400, 300, 0), projection with { AlongMetres = Math.Round(projection.AlongMetres), OffsetMetres = Math.Round(projection.OffsetMetres) });
    }

    [Fact]
    public void A_window_past_the_end_gives_the_last_point()
    {
        var projection = Project(Shape((0, 0), (1000, 0)), 200, 0, fromMetres: 5000);

        Assert.Equal(1000, projection.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_is_rejected()
    {
        var shape = Shape((0, 0), (1000, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => Project(shape, 0, 0, fromMetres: 500, toMetres: 100));
    }

    [Fact]
    public void A_shape_needs_two_points_and_a_longitude_for_each_latitude()
    {
        Assert.Throws<ArgumentException>(() => new RouteShape([60.0], [25.0]));
        Assert.Throws<ArgumentException>(() => new RouteShape([60.0, 60.1], [25.0]));
    }

    private static ShapeProjection Project(
        RouteShape shape,
        double east,
        double north,
        double fromMetres = 0,
        double toMetres = double.PositiveInfinity) =>
        shape.Project(Lat(north), Lon(east), fromMetres, toMetres);
}

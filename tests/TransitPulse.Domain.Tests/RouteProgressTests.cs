using static TransitPulse.Domain.Tests.LocalMetres;

namespace TransitPulse.Domain.Tests;

public sealed class RouteProgressTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly RouteShape _straight = Shape((0, 0), (3000, 0));

    // Out along one side of a street and back along the other, 20 m apart.
    private static readonly RouteShape _outAndBack = Shape((0, 0), (1000, 0), (1000, 20), (0, 20));

    [Fact]
    public void A_vehicle_moves_along_the_line()
    {
        var progress = new RouteProgress();

        var first = Advance(progress, Trip(_straight), 100, 5, 0);
        var second = Advance(progress, Trip(_straight), 300, -5, 10);

        Assert.Equal(new RouteMatch(RouteMatchStatus.OnRoute, 100, 5), Rounded(first));
        Assert.Equal(new RouteMatch(RouteMatchStatus.OnRoute, 300, 5), Rounded(second));
    }

    [Fact]
    public void A_small_step_back_is_noise_and_keeps_the_distance()
    {
        var progress = new RouteProgress();
        Advance(progress, Trip(_straight), 500, 0, 0);

        var match = Advance(progress, Trip(_straight), 480, 0, 1);

        Assert.Equal(RouteMatchStatus.OnRoute, match.Status);
        Assert.Equal(500, match.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_point_far_from_the_line_is_off_route_and_changes_nothing()
    {
        var progress = new RouteProgress();
        Advance(progress, Trip(_straight), 500, 0, 0);

        var off = Advance(progress, Trip(_straight), 700, 200, 5);
        var back = Advance(progress, Trip(_straight), 600, 0, 10);

        Assert.Equal(RouteMatchStatus.OffRoute, off.Status);
        Assert.Equal(500, off.AlongMetres, tolerance: 0.5);
        Assert.Equal(200, off.OffsetMetres, tolerance: 0.5);
        Assert.Equal(600, back.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void After_a_detour_the_window_has_grown_since_the_last_point_on_the_line()
    {
        var progress = new RouteProgress();
        Advance(progress, Trip(_straight), 500, 0, 0);
        Advance(progress, Trip(_straight), 1200, 300, 25);

        // 1 500 m in 30 s fits the window from the point on the line (500 + 60 × 30 + 100), not from the detour's point.
        var rejoined = Advance(progress, Trip(_straight), 2000, 0, 30);

        Assert.Equal(RouteMatchStatus.OnRoute, rejoined.Status);
        Assert.Equal(2000, rejoined.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void On_the_way_back_a_point_nearer_the_way_out_stays_on_the_way_back()
    {
        var progress = new RouteProgress();
        Advance(progress, Trip(_outAndBack), 500, 0, 0);
        Advance(progress, Trip(_outAndBack), 1000, 10, 10);

        var onTheWayBack = Advance(progress, Trip(_outAndBack), 700, 8, 20);
        var seenFirst = Advance(new RouteProgress(), Trip(_outAndBack), 700, 8, 20);

        Assert.Equal(1320, onTheWayBack.AlongMetres, tolerance: 0.5);
        Assert.Equal(700, seenFirst.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_new_trip_is_searched_along_its_whole_line()
    {
        var progress = new RouteProgress();
        Advance(progress, Trip(_straight), 1500, 0, 0);

        var nextTrip = Advance(progress, Trip(_straight, "t2"), 100, 0, 600);

        Assert.Equal(100, nextTrip.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_shape_from_a_new_snapshot_starts_the_search_over()
    {
        var progress = new RouteProgress();
        Advance(progress, Trip(_straight), 1500, 0, 0);

        var sameRunNewShape = Advance(progress, Trip(Shape((0, 0), (3000, 0))), 100, 0, 1);

        Assert.Equal(100, sameRunNewShape.AlongMetres, tolerance: 0.5);
    }

    [Fact]
    public void A_trip_without_a_shape_cannot_be_placed()
    {
        var match = Advance(new RouteProgress(), Trip(null), 100, 0, 0);

        Assert.Equal(new RouteMatch(RouteMatchStatus.NoShape, 0, 0), match);
    }

    [Fact]
    public void A_first_point_off_the_line_leaves_the_next_one_searched_along_the_whole_line()
    {
        var progress = new RouteProgress();

        var off = Advance(progress, Trip(_straight), 1500, 300, 0);
        var on = Advance(progress, Trip(_straight), 1500, 0, 1);

        Assert.Equal(new RouteMatch(RouteMatchStatus.OffRoute, 0, 300), Rounded(off));
        Assert.Equal(1500, on.AlongMetres, tolerance: 0.5);
    }

    private static ScheduledTrip Trip(RouteShape? shape, string tripId = "t1") =>
        new(tripId, "1009", 0, 12 * 3600, new DateOnly(2026, 11, 4), shape);

    private static RouteMatch Advance(RouteProgress progress, ScheduledTrip trip, double east, double north, int seconds) =>
        progress.Advance(trip, Lat(north), Lon(east), _start.AddSeconds(seconds));

    private static RouteMatch Rounded(RouteMatch match) =>
        match with { AlongMetres = Math.Round(match.AlongMetres), OffsetMetres = Math.Round(match.OffsetMetres) };
}

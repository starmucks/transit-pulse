using System.Globalization;

namespace TransitPulse.Domain.Tests;

/// <summary>
/// Real HSL tracks placed on their trips' lines (<c>Fixtures/tracks/README.md</c>). Bounds are the measured results with
/// some room, so that tuning thresholds later does not break them, while a regression does.
/// </summary>
public sealed class RouteFixtureTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("bus")]
    [InlineData("tram")]
    [InlineData("metro")]
    [InlineData("loop")]
    [InlineData("detour")]
    [InlineData("terminal")]
    public void The_distance_never_goes_back_unless_the_vehicle_is_placed_anew(string name)
    {
        var run = Run(name);

        for (var i = 1; i < run.Matches.Count; i++)
        {
            var (previous, current) = (run.Matches[i - 1].Match, run.Matches[i].Match);
            Assert.True(current.Reanchored || current.AlongMetres >= previous.AlongMetres, $"{name}: back at {run.Matches[i].Seconds} s");
        }
    }

    [Theory]
    [InlineData("bus", 1)]
    [InlineData("tram", 5)]
    [InlineData("loop", 5)]
    public void A_trip_on_its_line_stays_on_it_to_the_end(string name, double endMetres)
    {
        var run = Run(name);

        Assert.All(run.Matches, m => Assert.Equal(RouteMatchStatus.OnRoute, m.Match.Status));
        Assert.InRange(run.Last.AlongMetres, run.LengthMetres - endMetres, run.LengthMetres);
    }

    [Fact]
    public void A_metro_fix_at_a_station_outside_the_window_does_not_move_the_train()
    {
        var run = Run("metro");

        // Measured: 1 fix of 2 766 outside the window; the last fix is 112 m before the shape's end.
        Assert.InRange(run.Matches.Count(m => m.Match.Status != RouteMatchStatus.OnRoute), 0, 3);
        Assert.DoesNotContain(run.Matches, m => m.Match.Reanchored);
        Assert.InRange(run.Last.AlongMetres, run.LengthMetres - 150, run.LengthMetres);
    }

    [Fact]
    public void A_detour_is_one_run_off_route_and_the_bus_rejoins_its_line()
    {
        var run = Run("detour");
        var off = run.Matches.Select((m, i) => (m.Match.Status, i)).Where(m => m.Status == RouteMatchStatus.OffRoute).Select(m => m.i).ToList();

        // Measured: 245 fixes off route, all in a row.
        Assert.InRange(off.Count, 200, 260);
        Assert.Equal(off.Count, off[^1] - off[0] + 1);
        Assert.DoesNotContain(run.Matches, m => m.Match.Status == RouteMatchStatus.OutsideWindow || m.Match.Reanchored);
        Assert.InRange(run.Last.AlongMetres, run.LengthMetres - 1, run.LengthMetres);
    }

    [Fact]
    public void A_wrong_first_placement_at_the_terminal_is_corrected_within_a_minute()
    {
        var run = Run("terminal");
        var reanchored = Assert.Single(run.Matches, m => m.Match.Reanchored);
        var after = run.Matches.SkipWhile(m => !m.Match.Reanchored);

        // Measured: first fix at 231 m, placed anew at 23 s back to 0 m; before re-anchoring the bus was off route for 192 s.
        Assert.InRange(reanchored.Seconds, 0, 60);
        Assert.InRange(reanchored.Match.AlongMetres, 0, 20);
        Assert.All(after, m => Assert.Equal(RouteMatchStatus.OnRoute, m.Match.Status));
        Assert.InRange(run.Last.AlongMetres, run.LengthMetres - 20, run.LengthMetres);
    }

    // Each row goes through VehicleTrack first, as in the Processor: RouteProgress never sees a repeat or an impossible jump.
    private static TrackRun Run(string name)
    {
        var points = Rows($"{name}.shape.csv");
        var shape = new RouteShape([.. points.Select(p => p[0])], [.. points.Select(p => p[1])]);
        var trip = new ScheduledTrip(name, name, 0, 0, DateOnly.FromDateTime(_start.Date), shape);
        var (track, progress, matches) = (new VehicleTrack(), new RouteProgress(), new List<(double, RouteMatch)>());

        foreach (var row in Rows($"{name}.track.csv"))
        {
            var at = _start.AddSeconds(row[0]);
            if (track.Check(new VehiclePosition("hsl", name, null, null, row[1], row[2], null, null, null, null, at)) == PositionCheck.Accepted)
            {
                matches.Add((row[0], progress.Advance(trip, row[1], row[2], at)));
            }
        }

        return new TrackRun(matches, shape.LengthMetres);
    }

    private static List<double[]> Rows(string file) =>
        [.. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks", file))
            .Skip(1)
            .Select(line => Array.ConvertAll(line.Split(','), v => double.Parse(v, CultureInfo.InvariantCulture)))];

    private sealed record TrackRun(IReadOnlyList<(double Seconds, RouteMatch Match)> Matches, double LengthMetres)
    {
        public RouteMatch Last => Matches[^1].Match;
    }
}

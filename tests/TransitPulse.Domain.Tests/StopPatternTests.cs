using System.Globalization;
using static TransitPulse.Domain.Tests.LocalMetres;

namespace TransitPulse.Domain.Tests;

/// <summary>
/// Stops placed on their trip's line: synthetic lines for the rules, the real HSL trips of <c>Fixtures/tracks</c> (with
/// their <c>*.stops.csv</c>) for what the rules meet in practice.
/// </summary>
public sealed class StopPatternTests
{
    [Fact]
    public void A_stop_on_a_street_driven_both_ways_is_placed_on_the_pass_after_the_previous_stop()
    {
        // Out to 1 000 m and back along the same street; the third stop is on the way back.
        var shape = Shape((0, 0), (1000, 0), (0, 0));
        var stops = new[] { Stop(1, 0, 5), Stop(2, 1000, 5), Stop(3, 300, 5) };

        var calls = new StopPattern(shape, stops).Calls;

        // The nearest point of the whole line is on the way out, at 300 m.
        Assert.Equal(300, shape.Project(Lat(5), Lon(300)).AlongMetres, 1);
        Assert.Equal(1700, calls[2].AlongMetres, 1);
        Assert.Equal(5, calls[2].OffsetMetres, 1);
    }

    [Fact]
    public void A_stop_far_from_the_line_is_placed_no_earlier_than_the_previous_stop_and_shows_its_distance()
    {
        var shape = Shape((0, 0), (1000, 0));
        var stops = new[] { Stop(1, 500, 0), Stop(2, 200, 100) };

        var far = new StopPattern(shape, stops).Calls[1];

        Assert.Equal(500, far.AlongMetres, 1);
        Assert.True(far.OffsetMetres > StopPattern.NearLineMetres, $"offset {far.OffsetMetres} m");
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void Stops_out_of_sequence_order_are_rejected(int first, int second)
    {
        var shape = Shape((0, 0), (1000, 0));

        Assert.Throws<ArgumentException>(() => new StopPattern(shape, [Stop(first, 0, 0), Stop(second, 500, 0)]));
    }

    [Theory]
    [InlineData("bus")]
    [InlineData("tram")]
    [InlineData("metro")]
    [InlineData("loop")]
    [InlineData("detour")]
    [InlineData("terminal")]
    [InlineData("train")]
    public void Every_stop_of_a_real_trip_is_near_its_line_in_order_from_start_to_end(string name)
    {
        var (shape, calls, _) = Place(name);

        // Measured: at most 16.3 m off the line (metro), first stop within 1 m of the start, last within 8 m of the end.
        Assert.All(calls, c => Assert.InRange(c.OffsetMetres, 0, 20));
        Assert.All(calls.Zip(calls.Skip(1)), p => Assert.True(p.Second.AlongMetres >= p.First.AlongMetres, $"{name} #{p.Second.Sequence}"));
        Assert.InRange(calls[0].AlongMetres, 0, 5);
        Assert.InRange(calls[^1].AlongMetres, shape.LengthMetres - 15, shape.LengthMetres);
    }

    // shape_dist_traveled is HSL's own measure, on a slightly different scale (0.987–1.010 of ours), so it is scaled to
    // match at the last stop. Metro is left out: its distances part from the published shape by up to 275 m mid-line,
    // while every metro stop lies within 17 m of it, so they were not measured along these points.
    [Theory]
    [InlineData("bus")]
    [InlineData("tram")]
    [InlineData("loop")]
    [InlineData("detour")]
    [InlineData("terminal")]
    [InlineData("train")]
    public void Stops_land_where_the_feed_measures_them_along_the_line(string name)
    {
        var (_, calls, reference) = Place(name);
        var scale = calls[^1].AlongMetres / reference[^1];

        // Measured: at most 43 m apart (bus).
        Assert.All(calls.Zip(reference), p => Assert.InRange(p.First.AlongMetres - (p.Second * scale), -60, 60));
    }

    private static StopPoint Stop(int sequence, double east, double north) => new($"s{sequence}", sequence, Lat(north), Lon(east));

    private static (RouteShape Shape, IReadOnlyList<StopCall> Calls, List<double> ReferenceMetres) Place(string name)
    {
        var points = Rows($"{name}.shape.csv").Select(r => Array.ConvertAll(r, Number)).ToList();
        var shape = new RouteShape([.. points.Select(p => p[0])], [.. points.Select(p => p[1])]);

        // sequence,stop_id,lat,lon,shape_dist_traveled (HSL: kilometres)
        var rows = Rows($"{name}.stops.csv").ToList();
        StopPoint[] stops = [.. rows.Select(r => new StopPoint(r[1], int.Parse(r[0], CultureInfo.InvariantCulture), Number(r[2]), Number(r[3])))];
        return (shape, new StopPattern(shape, stops).Calls, [.. rows.Select(r => Number(r[4]) * 1000)]);
    }

    private static IEnumerable<string[]> Rows(string file) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks", file)).Skip(1).Select(line => line.Split(','));

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
}

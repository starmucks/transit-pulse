namespace TransitPulse.Domain.Tests;

/// <summary>
/// Stop events found on the real HSL tracks of <c>Fixtures/tracks</c>, compared with HSL's own events for the same trips
/// (<c>*.events.csv</c>; metro has none). Bounds are the measured results with room: a regression breaks them, tuning the
/// thresholds against an hour of the archive (task 2.7) should not.
/// </summary>
/// <remarks>
/// Measured with the first thresholds (zone 30 m, standing below 1 m/s), 135 stops of 6 trips: 128 agree on whether the
/// vehicle stood; 4 differ (two trams standing over 20 s that HSL reports as passes, a bus queueing farther than the zone
/// before its stop, a train standing on a long platform); 3 have no event (a train and a tram at their last stop, the
/// terminal fixture's first stop, whose departure re-anchoring misses as ADR-0011 expects); 1 event at a stop without
/// HSL's. Arrivals within 2 s of HSL's but one (median 0.9 s later); departures and passes earlier, by 2.3 and 4.7 s at the
/// median: HSL's own radius ends farther past the stop than the zone.
/// </remarks>
public sealed class StopFixtureTests
{
    private static readonly Lazy<Comparison[]> _all = new(() => [.. new[] { "bus", "tram", "loop", "detour", "terminal", "train" }.Select(Compare)]);

    [Fact]
    public void The_detector_and_HSL_agree_on_whether_the_vehicle_stood_at_most_stops()
    {
        var all = _all.Value;

        Assert.Equal(135, all.Sum(c => c.Reference));
        Assert.InRange(all.Sum(c => c.Agree), 120, 135);
        Assert.InRange(all.Sum(c => c.Missed), 0, 6);
        Assert.InRange(all.Sum(c => c.Extra), 0, 3);
    }

    [Fact]
    public void Arrivals_are_within_two_seconds_of_HSL()
    {
        var arrivals = _all.Value.SelectMany(c => c.Arrival).ToList();

        // Measured: 68 of 69 within 2 s; the other is a tram 19.6 s early.
        Assert.InRange(arrivals.Count, 60, 80);
        Assert.InRange(arrivals.Count(d => Math.Abs(d) <= 2) / (double)arrivals.Count, 0.95, 1);
    }

    [Fact]
    public void Departures_and_passes_are_within_ten_seconds_of_HSL_at_the_median()
    {
        var departures = _all.Value.SelectMany(c => c.Departure).ToList();
        var passes = _all.Value.SelectMany(c => c.Pass).ToList();

        // Measured medians: departures -2.3 s (71), passes -4.7 s (55): HSL's events come when its radius ends.
        Assert.InRange(departures.Count, 60, 80);
        Assert.InRange(passes.Count, 45, 65);
        Assert.InRange(Median(departures), -10, 10);
        Assert.InRange(Median(passes), -10, 10);
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    // Per stop of the trip: HSL stood if it sent ars, pde or wait, and passed if it sent pas; the detector stood if it found
    // an arrival or a departure. Times are compared for the matching kinds: ours minus HSL's, in seconds.
    private static Comparison Compare(string name)
    {
        var run = TrackFixture.Run(name);
        var reference = TrackFixture.Rows($"{name}.events.csv")
            .GroupBy(r => r[2])
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r[1]).ToDictionary(k => k.Key, k => TrackFixture.Number(k.First()[0])));
        var ours = run.Stops
            .GroupBy(e => e.Stop.StopId)
            .ToDictionary(g => g.Key, g => g.GroupBy(e => e.Kind).ToDictionary(k => k.Key, k => TrackRun.Seconds(k.Last())));

        var c = new Comparison { Reference = reference.Count, Extra = ours.Keys.Count(stop => !reference.ContainsKey(stop)) };
        foreach (var (stop, theirs) in reference)
        {
            if (!ours.TryGetValue(stop, out var mine))
            {
                c.Missed++;
                continue;
            }

            var theyStood = theirs.ContainsKey("ars") || theirs.ContainsKey("pde") || theirs.ContainsKey("wait");
            var weStood = mine.ContainsKey(StopEventKind.Arrival) || mine.ContainsKey(StopEventKind.Departure);
            if (theyStood == weStood)
            {
                c.Agree++;
            }

            Add(c.Arrival, mine, StopEventKind.Arrival, theirs, "ars");
            Add(c.Departure, mine, StopEventKind.Departure, theirs, "dep");
            Add(c.Pass, mine, StopEventKind.Passed, theirs, "pas");
        }

        return c;
    }

    private static void Add(List<double> diffs, Dictionary<StopEventKind, double> mine, StopEventKind kind, Dictionary<string, double> theirs, string hslEvent)
    {
        if (mine.TryGetValue(kind, out var at) && theirs.TryGetValue(hslEvent, out var reference))
        {
            diffs.Add(at - reference);
        }
    }

    private sealed class Comparison
    {
        public int Reference { get; init; }

        public int Extra { get; init; }

        public int Agree { get; set; }

        public int Missed { get; set; }

        public List<double> Arrival { get; } = [];

        public List<double> Departure { get; } = [];

        public List<double> Pass { get; } = [];
    }
}

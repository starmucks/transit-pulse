using System.Globalization;

namespace TransitPulse.Domain.Tests;

/// <summary>
/// Real HSL tracks of <c>Fixtures/tracks</c> run through what the Processor runs per vehicle: <see cref="VehicleTrack"/>,
/// then <see cref="RouteProgress"/> and <see cref="StopDetector"/> for each accepted position.
/// </summary>
internal static class TrackFixture
{
    /// <summary>What 0 s in the fixture files maps to; the day is the trip's service day.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    public static TrackRun Run(string name)
    {
        var points = Rows($"{name}.shape.csv").Select(r => Array.ConvertAll(r, Number)).ToList();
        var shape = new RouteShape([.. points.Select(p => p[0])], [.. points.Select(p => p[1])]);
        var stops = Rows($"{name}.stops.csv").Select(r => new StopPoint(r[1], int.Parse(r[0], CultureInfo.InvariantCulture), Number(r[2]), Number(r[3])));
        var trip = new ScheduledTrip(name, name, 0, 0, DateOnly.FromDateTime(Start.Date), shape, new StopPattern(shape, [.. stops]));
        var (track, progress, detector) = (new VehicleTrack(), new RouteProgress(), new StopDetector());
        var (matches, events) = (new List<(double, RouteMatch)>(), new List<StopEvent>());

        foreach (var row in Rows($"{name}.track.csv").Select(r => Array.ConvertAll(r, Number)))
        {
            var at = Start.AddSeconds(row[0]);
            if (track.Check(new VehiclePosition("hsl", name, null, null, row[1], row[2], null, null, null, null, at)) == PositionCheck.Accepted)
            {
                var match = progress.Advance(trip, row[1], row[2], at);
                matches.Add((row[0], match));
                detector.Advance(trip, match, at, events);
            }
        }

        return new TrackRun(matches, events, shape.LengthMetres);
    }

    /// <summary>The rows of a fixture file without its header, split into fields.</summary>
    public static IEnumerable<string[]> Rows(string file) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks", file)).Skip(1).Select(line => line.Split(','));

    public static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>One fixture run: each accepted position's placement with its seconds, and the stop events found.</summary>
internal sealed record TrackRun(IReadOnlyList<(double Seconds, RouteMatch Match)> Matches, IReadOnlyList<StopEvent> Stops, double LengthMetres)
{
    public RouteMatch Last => Matches[^1].Match;

    /// <summary>When a stop event happened, in the fixture's seconds.</summary>
    public static double Seconds(StopEvent e) => (e.At - TrackFixture.Start).TotalSeconds;
}

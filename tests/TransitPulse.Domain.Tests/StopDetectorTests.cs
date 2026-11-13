using static TransitPulse.Domain.Tests.LocalMetres;

namespace TransitPulse.Domain.Tests;

public sealed class StopDetectorTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly RouteShape _straight = Shape((0, 0), (3000, 0));

    [Fact]
    public void A_vehicle_that_drives_in_and_stands_arrives_and_then_departs_at_the_end_of_the_zone()
    {
        var events = Drive(Trip(500), (0, 440), (2, 465), (3, 475), (4, 475.5), (20, 476), (22, 485), (27, 535));

        // Stood from 3 s; left the zone (530 m) nine tenths of the way from 485 m at 22 s to 535 m at 27 s.
        Assert.Equal([(StopEventKind.Arrival, "A", 3.0), (StopEventKind.Departure, "A", 26.5)], events);
    }

    [Fact]
    public void A_vehicle_that_does_not_stand_passes_the_stop_when_it_crosses_it()
    {
        var events = Drive(Trip(500), (0, 440), (4, 480), (8, 520), (12, 560));

        Assert.Equal([(StopEventKind.Passed, "A", 6.0)], events);
    }

    [Fact]
    public void Close_stops_split_the_line_between_them()
    {
        // Zones: A 470-515 m, B 515-560 m. The bus crosses A at 6 s and stands at 520 m, in B's zone.
        var events = Drive(Trip(500, 530), (0, 440), (4, 480), (7, 510), (8, 520), (9, 520.5));

        Assert.Equal([(StopEventKind.Passed, "A", 6.0), (StopEventKind.Arrival, "B", 8.0)], events);
    }

    [Fact]
    public void One_move_across_two_zones_passes_both_stops()
    {
        var events = Drive(Trip(500, 600), (0, 440), (10, 660));

        Assert.Equal([(StopEventKind.Passed, "A", S(60 / 22.0)), (StopEventKind.Passed, "B", S(160 / 22.0))], events);
    }

    [Fact]
    public void A_stop_crossed_during_a_long_gap_has_no_event()
    {
        var events = Drive(Trip(500), (0, 440), (40, 660));

        Assert.Empty(events);
    }

    [Fact]
    public void A_vehicle_first_seen_standing_in_the_zone_only_departs()
    {
        var events = Drive(Trip(500), (0, 495), (1, 495), (30, 495.5), (35, 540));

        Assert.Equal([(StopEventKind.Departure, "A", S(30 + (5 * 34.5 / 44.5)))], events);
    }

    [Fact]
    public void Placed_anew_back_the_vehicle_finds_the_stops_again()
    {
        var trip = Trip(500, 1500);
        var (detector, events) = (new StopDetector(), new List<StopEvent>());
        Advance(detector, trip, OnRoute(1400), 0, events);
        Advance(detector, trip, OnRoute(1450), 5, events);
        Advance(detector, trip, OnRoute(1560), 10, events);

        Advance(detector, trip, OnRoute(300) with { ReanchoredByMetres = -1260 }, 15, events);
        Advance(detector, trip, OnRoute(400), 20, events);
        Advance(detector, trip, OnRoute(600), 30, events);

        Assert.Equal([(StopEventKind.Passed, "B", S(5 + (5 * 50 / 110.0))), (StopEventKind.Passed, "A", 25.0)], Rounded(events));
    }

    [Fact]
    public void Placed_anew_forward_the_vehicle_skips_the_stops_between_without_events()
    {
        var trip = Trip(500, 1500);
        var (detector, events) = (new StopDetector(), new List<StopEvent>());
        Advance(detector, trip, OnRoute(300), 0, events);

        Advance(detector, trip, OnRoute(1400) with { ReanchoredByMetres = 1100 }, 10, events);
        Advance(detector, trip, OnRoute(1450), 15, events);
        Advance(detector, trip, OnRoute(1560), 20, events);

        Assert.Equal([(StopEventKind.Passed, "B", S(15 + (5 * 50 / 110.0)))], Rounded(events));
    }

    [Fact]
    public void A_new_trip_starts_over_and_needs_its_own_drive_into_the_zone()
    {
        var (detector, events) = (new StopDetector(), new List<StopEvent>());
        Advance(detector, Trip(500), OnRoute(440), 0, events);
        Advance(detector, Trip(500), OnRoute(480), 4, events);

        var next = Trip(500) with { TripId = "next" };
        Advance(detector, next, OnRoute(485), 5, events);
        Advance(detector, next, OnRoute(485), 6, events);

        Assert.Empty(events);
    }

    [Fact]
    public void A_position_off_the_line_changes_nothing()
    {
        var trip = Trip(500);
        var (detector, events) = (new StopDetector(), new List<StopEvent>());
        Advance(detector, trip, OnRoute(440), 0, events);
        Advance(detector, trip, new RouteMatch(RouteMatchStatus.OffRoute, 900, 80), 2, events);
        Advance(detector, trip, new RouteMatch(RouteMatchStatus.OutsideWindow, 900, 10), 3, events);

        Assert.Empty(events);

        Advance(detector, trip, OnRoute(480), 4, events);
        Advance(detector, trip, OnRoute(520), 8, events);
        Advance(detector, trip, OnRoute(560), 12, events);

        Assert.Equal([(StopEventKind.Passed, "A", 6.0)], Rounded(events));
    }

    [Fact]
    public void A_trip_without_stops_has_no_events()
    {
        var (detector, events) = (new StopDetector(), new List<StopEvent>());
        var trip = new ScheduledTrip("t", "r", 0, 0, DateOnly.FromDateTime(_start.Date), _straight);

        Advance(detector, trip, OnRoute(440), 0, events);
        Advance(detector, trip, OnRoute(660), 10, events);

        Assert.Empty(events);
    }

    // Stops named A, B, ... at the given distances along a straight line.
    private static ScheduledTrip Trip(params double[] stopMetres)
    {
        var stops = stopMetres.Select((m, i) => new StopPoint(((char)('A' + i)).ToString(), i + 1, Lat(0), Lon(m))).ToArray();
        return new ScheduledTrip("t", "r", 0, 0, DateOnly.FromDateTime(_start.Date), _straight, new StopPattern(_straight, stops));
    }

    private static RouteMatch OnRoute(double along) => new(RouteMatchStatus.OnRoute, along, 0);

    private static void Advance(StopDetector detector, ScheduledTrip trip, RouteMatch match, double seconds, List<StopEvent> events) =>
        detector.Advance(trip, match, _start.AddSeconds(seconds), events);

    private static List<(StopEventKind, string, double)> Drive(ScheduledTrip trip, params (double Seconds, double Along)[] steps)
    {
        var (detector, events) = (new StopDetector(), new List<StopEvent>());
        foreach (var (seconds, along) in steps)
        {
            Advance(detector, trip, OnRoute(along), seconds, events);
        }

        return Rounded(events);
    }

    // Times to the millisecond: interpolation goes through ticks, the expected values through doubles.
    private static double S(double seconds) => Math.Round(seconds, 3);

    private static List<(StopEventKind, string, double)> Rounded(List<StopEvent> events) =>
        [.. events.Select(e => (e.Kind, e.Stop.StopId, S((e.At - _start).TotalSeconds)))];
}

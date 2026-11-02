namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// Decides whether a GTFS snapshot is whole enough to replace the current schedule. Fed row by row in one pass, so an
/// importer can check while it copies: references are checked as rows arrive, which needs routes, stops, calendars and
/// shapes before trips, and trips before stop times — the order <see cref="Run"/> reads in.
/// </summary>
/// <remarks>
/// Broken exports happen: HSL's of 2026-10-27 had empty routes, stops, trips and shapes files. Problems are counted per
/// rule with the first example, since one broken rule can repeat millions of times.
/// </remarks>
public sealed class GtfsSnapshotCheck
{
    private readonly HashSet<string> _routes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stops = new(StringComparer.Ordinal);
    private readonly HashSet<string> _services = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _callsPerTrip = new(StringComparer.Ordinal);
    private readonly List<(string Stop, string Parent)> _parents = [];
    private readonly Dictionary<string, long> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Count, string Example)> _problems = new(StringComparer.Ordinal);
    private bool _aborted;

    /// <summary>Reads a whole snapshot in reference order and checks it.</summary>
    /// <param name="feed">The snapshot.</param>
    /// <returns>Row counts and problems; reading stops at the first row that breaks the format.</returns>
    public static GtfsSnapshotReport Run(GtfsFeed feed)
    {
        var check = new GtfsSnapshotCheck();
        try
        {
            ReadAll(feed.Routes(), check.Add);
            ReadAll(feed.Stops(), check.Add);
            var hasCalendar = feed.Contains("calendar.txt");
            var hasDates = feed.Contains("calendar_dates.txt");
            if (!hasCalendar && !hasDates)
            {
                check.Problem("neither calendar.txt nor calendar_dates.txt is present", "");
            }

            if (hasCalendar)
            {
                ReadAll(feed.Calendars(), check.Add);
            }

            if (hasDates)
            {
                ReadAll(feed.CalendarDates(), check.Add);
            }

            ReadAll(feed.ShapePoints(), check.Add);
            ReadAll(feed.Trips(), check.Add);
            ReadAll(feed.StopTimes(), check.Add);
        }
        catch (GtfsFormatException ex)
        {
            check.Abort(ex);
        }

        return check.Finish();
    }

    /// <summary>Checks a route.</summary>
    /// <param name="route">Row of <c>routes.txt</c>.</param>
    public void Add(GtfsRoute route) => AddId("routes.txt", _routes, route.RouteId);

    /// <summary>Checks a stop; its parent station is checked at the end, as it may come later in the file.</summary>
    /// <param name="stop">Row of <c>stops.txt</c>.</param>
    public void Add(GtfsStop stop)
    {
        AddId("stops.txt", _stops, stop.StopId);
        if (stop.ParentStation is { } parent)
        {
            _parents.Add((stop.StopId, parent));
        }
    }

    /// <summary>Registers a service.</summary>
    /// <param name="calendar">Row of <c>calendar.txt</c>.</param>
    public void Add(GtfsCalendar calendar)
    {
        Count("calendar.txt");
        _services.Add(calendar.ServiceId);
    }

    /// <summary>Registers a service; one may exist only in <c>calendar_dates.txt</c>.</summary>
    /// <param name="date">Row of <c>calendar_dates.txt</c>.</param>
    public void Add(GtfsCalendarDate date)
    {
        Count("calendar_dates.txt");
        _services.Add(date.ServiceId);
    }

    /// <summary>Registers a shape.</summary>
    /// <param name="point">Row of <c>shapes.txt</c>.</param>
    public void Add(GtfsShapePoint point)
    {
        Count("shapes.txt");
        _shapes.Add(point.ShapeId);
    }

    /// <summary>Checks a trip against routes, services and shapes added before it.</summary>
    /// <param name="trip">Row of <c>trips.txt</c>.</param>
    public void Add(GtfsTrip trip)
    {
        Count("trips.txt");
        if (!_callsPerTrip.TryAdd(trip.TripId, 0))
        {
            Problem("trips.txt: duplicate trip_id", trip.TripId);
        }

        if (!_routes.Contains(trip.RouteId))
        {
            Problem("trips.txt: route_id not in routes.txt", $"{trip.TripId} -> {trip.RouteId}");
        }

        if (!_services.Contains(trip.ServiceId))
        {
            Problem("trips.txt: service_id in neither calendar", $"{trip.TripId} -> {trip.ServiceId}");
        }

        if (trip.ShapeId is { } shape && !_shapes.Contains(shape))
        {
            Problem("trips.txt: shape_id not in shapes.txt", $"{trip.TripId} -> {shape}");
        }
    }

    /// <summary>Checks a stop time against trips and stops added before it.</summary>
    /// <param name="time">Row of <c>stop_times.txt</c>.</param>
    public void Add(GtfsStopTime time)
    {
        Count("stop_times.txt");
        if (_callsPerTrip.TryGetValue(time.TripId, out var calls))
        {
            _callsPerTrip[time.TripId] = calls + 1;
        }
        else
        {
            Problem("stop_times.txt: trip_id not in trips.txt", time.TripId);
        }

        if (!_stops.Contains(time.StopId))
        {
            Problem("stop_times.txt: stop_id not in stops.txt", $"{time.TripId} #{time.StopSequence} -> {time.StopId}");
        }
    }

    /// <summary>Records a problem found outside the row checks, such as a file that breaks the format.</summary>
    /// <param name="rule">What is wrong; problems of one rule are counted together.</param>
    /// <param name="example">The first case, to look it up.</param>
    public void Problem(string rule, string example)
    {
        var (count, first) = _problems.GetValueOrDefault(rule, (0, example));
        _problems[rule] = (count + 1, first);
    }

    /// <summary>Records that reading stopped at a row breaking the format; the snapshot is rejected.</summary>
    /// <param name="error">Why reading stopped.</param>
    public void Abort(GtfsFormatException error)
    {
        Problem(error.Message, "");
        _aborted = true;
    }

    /// <summary>Applies the checks that need the whole snapshot, unless reading was aborted.</summary>
    /// <returns>Row counts and problems.</returns>
    public GtfsSnapshotReport Finish()
    {
        // Files left unread would show up as empty and trips as stopless: noise around the one real problem.
        if (_aborted)
        {
            return Report();
        }

        foreach (var file in (string[])["routes.txt", "stops.txt", "shapes.txt", "trips.txt", "stop_times.txt"])
        {
            if (_rows.GetValueOrDefault(file) == 0)
            {
                Problem($"{file} has no rows", "");
            }
        }

        if (_services.Count == 0)
        {
            Problem("no service in either calendar", "");
        }

        foreach (var (trip, calls) in _callsPerTrip)
        {
            // A trip needs a first and a last stop; fewer calls cannot be a journey.
            if (calls < 2)
            {
                Problem("trips.txt: trip with fewer than 2 stop times", $"{trip} ({calls})");
            }
        }

        foreach (var (stop, parent) in _parents)
        {
            if (!_stops.Contains(parent))
            {
                Problem("stops.txt: parent_station not in stops.txt", $"{stop} -> {parent}");
            }
        }

        return Report();
    }

    private GtfsSnapshotReport Report() => new(
        new Dictionary<string, long>(_rows),
        [.. _problems.Select(pair => pair.Value.Example.Length == 0 ? pair.Key : $"{pair.Key}: {pair.Value.Count} (first: {pair.Value.Example})")]);

    private void AddId(string file, HashSet<string> ids, string id)
    {
        Count(file);
        if (!ids.Add(id))
        {
            Problem($"{file}: duplicate id", id);
        }
    }

    private void Count(string file) => _rows[file] = _rows.GetValueOrDefault(file) + 1;

    private static void ReadAll<T>(IEnumerable<T> rows, Action<T> add)
    {
        foreach (var row in rows)
        {
            add(row);
        }
    }
}

/// <summary>Outcome of a <see cref="GtfsSnapshotCheck"/>.</summary>
/// <param name="Rows">Rows read per file.</param>
/// <param name="Problems">One line per broken rule: what, how often, the first case.</param>
public sealed record GtfsSnapshotReport(IReadOnlyDictionary<string, long> Rows, IReadOnlyList<string> Problems)
{
    /// <summary>Whether the snapshot may replace the current schedule.</summary>
    public bool IsValid => Problems.Count == 0;
}

using System.Globalization;

namespace TransitPulse.Domain;

/// <summary>
/// The scheduled runs of a few service days, looked up by the HSL trip descriptor
/// <c>{route}/{direction_id}/{yyyyMMdd}/{HH:mm:ss}</c> that the Ingestor builds, because HFP carries no GTFS <c>trip_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// A descriptor's route is either a GTFS <c>route_id</c> or a route variant (<c>100HI9</c> of route <c>100H</c>). GTFS has no
/// field for variants; HSL's <c>trip_id</c> starts with one (<c>100HI9_20261103_Ke_1_1924</c>), an observed convention, not
/// a documented one. Measured over 15 days: without it 2.0 % of positions find no trip, and 25 % in the quiet hour at
/// 02:00 UTC (<c>docs/benchmarks.md</c>).
/// </para>
/// <para>
/// Several runs under one key stay unresolved rather than picking one: a wrong trip would produce wrong arrivals, and those
/// are the training labels of the prediction model.
/// </para>
/// <para>Immutable once built: one thread may build it while another keeps reading the previous one.</para>
/// </remarks>
public sealed class TripSchedule
{
    private const int SeveralRuns = -1;

    private readonly ScheduledTrip[] _trips;
    private readonly HashSet<int> _serviceDays;
    private readonly Dictionary<string, int> _routeCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _routeCodesBySpan;
    private readonly Dictionary<RunKey, int> _runs;

    /// <summary>Builds the lookup.</summary>
    /// <param name="serviceDates">The days the schedule holds, including those without any runs.</param>
    /// <param name="trips">The runs of those days.</param>
    /// <exception cref="ArgumentException">A run belongs to a day outside <paramref name="serviceDates"/>.</exception>
    public TripSchedule(IEnumerable<DateOnly> serviceDates, IEnumerable<ScheduledTrip> trips)
    {
        ArgumentNullException.ThrowIfNull(serviceDates);
        ArgumentNullException.ThrowIfNull(trips);

        _serviceDays = [.. serviceDates.Select(date => date.DayNumber)];
        _trips = [.. trips];
        _runs = new Dictionary<RunKey, int>(_trips.Length);
        _routeCodesBySpan = _routeCodes.GetAlternateLookup<ReadOnlySpan<char>>();

        for (var i = 0; i < _trips.Length; i++)
        {
            var trip = _trips[i];
            if (!_serviceDays.Contains(trip.ServiceDate.DayNumber))
            {
                throw new ArgumentException($"Trip {trip.TripId} runs on {trip.ServiceDate}, outside the given days.", nameof(trips));
            }

            Add(trip.RouteId, trip, i);

            var variant = Variant(trip.TripId);
            if (!variant.IsEmpty && !variant.SequenceEqual(trip.RouteId))
            {
                Add(variant, trip, i);
            }
        }
    }

    /// <summary>How many scheduled runs the schedule holds.</summary>
    public int RunCount => _trips.Length;

    /// <summary>Finds the scheduled run a descriptor refers to. Allocates nothing, so it can run for every message.</summary>
    /// <param name="descriptor">The position's trip id; <see langword="null"/> when it has none.</param>
    /// <param name="trip">The run when the result is <see cref="TripResolution.Found"/>, otherwise <see langword="null"/>.</param>
    /// <returns>Whether a single run was found, or why not.</returns>
    public TripResolution Find(string? descriptor, out ScheduledTrip? trip)
    {
        trip = null;
        if (descriptor is null)
        {
            return TripResolution.NoDescriptor;
        }

        if (!TryParse(descriptor, out var route, out var directionId, out var date, out var startSeconds))
        {
            return TripResolution.Malformed;
        }

        if (!_serviceDays.Contains(date.DayNumber))
        {
            return TripResolution.DayNotLoaded;
        }

        if (!_routeCodesBySpan.TryGetValue(route, out var routeCode))
        {
            return TripResolution.RouteUnknown;
        }

        if (!_runs.TryGetValue(new RunKey(routeCode, directionId, date.DayNumber, startSeconds), out var run))
        {
            return TripResolution.NoDeparture;
        }

        if (run == SeveralRuns)
        {
            return TripResolution.Ambiguous;
        }

        trip = _trips[run];
        return TripResolution.Found;
    }

    private void Add(ReadOnlySpan<char> route, ScheduledTrip trip, int index)
    {
        if (!_routeCodesBySpan.TryGetValue(route, out var routeCode))
        {
            routeCode = _routeCodes.Count;
            _routeCodesBySpan[route] = routeCode;
        }

        var key = new RunKey(routeCode, trip.DirectionId, trip.ServiceDate.DayNumber, trip.StartSeconds);
        if (!_runs.TryAdd(key, index))
        {
            _runs[key] = SeveralRuns;
        }
    }

    private static ReadOnlySpan<char> Variant(string tripId)
    {
        var separator = tripId.IndexOf('_', StringComparison.Ordinal);
        return separator > 0 ? tripId.AsSpan(0, separator) : default;
    }

    private static bool TryParse(
        ReadOnlySpan<char> text, out ReadOnlySpan<char> route, out int directionId, out DateOnly date, out int startSeconds)
    {
        route = default;
        directionId = 0;
        date = default;
        startSeconds = 0;

        // One more range than the four parts, so that a fifth part shows up in the count instead of joining the last one.
        Span<Range> parts = stackalloc Range[5];
        if (text.Split(parts, '/') != 4)
        {
            return false;
        }

        var direction = text[parts[1]];
        if (text[parts[0]].IsEmpty
            || direction is not "0" and not "1"
            || !DateOnly.TryParseExact(text[parts[2]], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || !TryParseTime(text[parts[3]], out startSeconds))
        {
            return false;
        }

        route = text[parts[0]];
        directionId = direction[0] - '0';
        return true;
    }

    // By hand, not TimeOnly: hours go past 24 on trips that start after midnight.
    private static bool TryParseTime(ReadOnlySpan<char> text, out int seconds)
    {
        seconds = 0;
        if (text.Length != 8 || text[2] != ':' || text[5] != ':'
            || !int.TryParse(text[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(text[3..5], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(text[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var secondsPart)
            || minutes > 59 || secondsPart > 59)
        {
            return false;
        }

        seconds = (hours * 3600) + (minutes * 60) + secondsPart;
        return true;
    }

    private readonly record struct RunKey(int RouteCode, int DirectionId, int ServiceDay, int StartSeconds);
}

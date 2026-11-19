using System.Runtime.CompilerServices;

namespace TransitPulse.Domain;

/// <summary>
/// Finds consecutive vehicles whose current gap, expressed as scheduled travel time, stays below a share of their planned
/// headway. One instance must see every vehicle it compares and be advanced from one thread.
/// </summary>
/// <remarks>
/// Vehicles are comparable only on the same feed, route, direction and shared stop pattern. Entry and recovery are both
/// confirmed over time so one noisy position cannot open or split an episode. GTFS arrival times interpolate distance into
/// seconds; positions outside the timed part of a trip are ignored.
/// </remarks>
public sealed class BunchingDetector
{
    /// <summary>Actual headway below this share of scheduled headway is bunching.</summary>
    public const double HeadwayRatio = 0.25;

    /// <summary>How long the condition must hold both to open and to close an episode.</summary>
    public const double ConfirmationSeconds = 30;

    /// <summary>How old another vehicle's position may be when compared.</summary>
    public const double PositionFreshnessSeconds = 5;

    private static readonly TimeSpan _confirmation = TimeSpan.FromSeconds(ConfirmationSeconds);
    private static readonly TimeSpan _freshness = TimeSpan.FromSeconds(PositionFreshnessSeconds);
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(ConfirmationSeconds);

    private readonly TimeZoneInfo _timeZone;
    private readonly Dictionary<LineKey, LineState> _lines = [];
    private readonly Dictionary<VehicleKey, LineKey> _lineOfVehicle = [];
    private readonly ConditionalWeakTable<StopTimetable, TimelineHolder> _timelines = new();
    private readonly List<Observation> _ordered = [];
    private readonly Dictionary<PairKey, PairMeasurement> _measurements = [];
    private readonly List<PairKey> _pairsToRecover = [];
    private readonly List<VehicleKey> _staleVehicles = [];
    private DateTimeOffset _latestAt;
    private DateTimeOffset _lastSweepAt;

    /// <summary>Creates a detector for one schedule feed.</summary>
    /// <param name="timeZone">The schedule feed's local time zone.</param>
    public BunchingDetector(TimeZoneInfo timeZone)
    {
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
    }

    /// <summary>Updates one on-route vehicle and reports newly confirmed episodes.</summary>
    /// <param name="position">The accepted source position.</param>
    /// <param name="trip">The scheduled trip the position resolved to.</param>
    /// <param name="alongMetres">Distance along the trip's shape.</param>
    /// <param name="events">Receives zero or more episodes confirmed by this update.</param>
    public void Advance(
        VehiclePosition position,
        ScheduledTrip trip,
        double alongMetres,
        ICollection<BunchingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(alongMetres);
        if (!double.IsFinite(alongMetres))
        {
            throw new ArgumentOutOfRangeException(nameof(alongMetres), alongMetres, "Distance must be finite.");
        }

        if (trip.Stops is not { } pattern || trip.Timetable is not { } timetable)
        {
            Remove(position.FeedId, position.VehicleId);
            return;
        }

        _latestAt = Max(_latestAt, position.ObservedAt);
        var vehicleKey = new VehicleKey(position.FeedId, position.VehicleId);
        var lineKey = new LineKey(position.FeedId, trip.RouteId, trip.DirectionId, pattern);
        if (_lineOfVehicle.TryGetValue(vehicleKey, out var previousKey) && previousKey != lineKey)
        {
            RemoveFromLine(vehicleKey, previousKey, _latestAt, events);
        }

        _lineOfVehicle[vehicleKey] = lineKey;
        if (!_lines.TryGetValue(lineKey, out var line))
        {
            line = new LineState();
            _lines.Add(lineKey, line);
        }

        line.Vehicles[vehicleKey] = new Observation(position, trip, timetable, alongMetres);
        PurgeStale(lineKey, line, _latestAt);
        Recalculate(line, _latestAt, events, allowConfirmation: true);
        SweepIfDue();
    }

    /// <summary>Forgets a vehicle, for example when its Kafka partition leaves this process.</summary>
    /// <param name="feedId">Source feed.</param>
    /// <param name="vehicleId">Vehicle identifier within the feed.</param>
    public void Remove(string feedId, string vehicleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);

        var vehicleKey = new VehicleKey(feedId, vehicleId);
        if (!_lineOfVehicle.Remove(vehicleKey, out var lineKey) || !_lines.TryGetValue(lineKey, out var line))
        {
            return;
        }

        line.Vehicles.Remove(vehicleKey);
        foreach (var pair in line.Episodes.Keys.Where(pair => pair.Contains(vehicleKey)).ToArray())
        {
            line.Episodes.Remove(pair);
        }

        RemoveLineIfEmpty(lineKey, line);
    }

    private void RemoveFromLine(
        VehicleKey vehicleKey,
        LineKey lineKey,
        DateTimeOffset at,
        ICollection<BunchingEvent> events)
    {
        if (!_lines.TryGetValue(lineKey, out var line))
        {
            return;
        }

        line.Vehicles.Remove(vehicleKey);
        PurgeStale(lineKey, line, at);
        Recalculate(line, at, events, allowConfirmation: true);
        RemoveLineIfEmpty(lineKey, line);
    }

    private void Recalculate(
        LineState line,
        DateTimeOffset at,
        ICollection<BunchingEvent>? events,
        bool allowConfirmation)
    {
        _ordered.Clear();
        _ordered.AddRange(line.Vehicles.Values);
        _ordered.Sort(ObservationComparer.Instance);
        _measurements.Clear();
        for (var i = 0; i + 1 < _ordered.Count; i++)
        {
            var follower = _ordered[i];
            var leader = _ordered[i + 1];
            if (TryMeasure(follower, leader, out var measured))
            {
                _measurements.Add(measured.Pair, measured);
            }
        }

        foreach (var (pair, measured) in _measurements)
        {
            if (measured.Ratio >= HeadwayRatio)
            {
                Recover(line, pair, at);
                continue;
            }

            if (!line.Episodes.TryGetValue(pair, out var episode))
            {
                episode = new EpisodeState(at);
                line.Episodes.Add(pair, episode);
            }

            episode.RecoveringSince = null;
            if (!episode.Active && allowConfirmation && at - episode.StartedAt >= _confirmation)
            {
                episode.Active = true;
                events!.Add(measured.Event(episode.StartedAt, at));
            }
        }

        _pairsToRecover.Clear();
        foreach (var pair in line.Episodes.Keys)
        {
            if (!_measurements.ContainsKey(pair))
            {
                _pairsToRecover.Add(pair);
            }
        }

        foreach (var pair in _pairsToRecover)
        {
            Recover(line, pair, at);
        }

        _ordered.Clear();
        _measurements.Clear();
        _pairsToRecover.Clear();
    }

    private static void Recover(LineState line, PairKey pair, DateTimeOffset at)
    {
        if (!line.Episodes.TryGetValue(pair, out var episode))
        {
            return;
        }

        if (!episode.Active)
        {
            line.Episodes.Remove(pair);
            return;
        }

        episode.RecoveringSince ??= at;
        if (at - episode.RecoveringSince >= _confirmation)
        {
            line.Episodes.Remove(pair);
        }
    }

    private bool TryMeasure(Observation follower, Observation leader, out PairMeasurement measurement)
    {
        measurement = default;
        var followerTimeline = TimelineOf(follower);
        var leaderTimeline = TimelineOf(leader);
        if (followerTimeline is null
            || leaderTimeline is null
            || !followerTimeline.TryAt(follower.AlongMetres, out var followerHere)
            || !followerTimeline.TryAt(leader.AlongMetres, out var followerThere)
            || !leaderTimeline.TryAt(follower.AlongMetres, out var leaderHere))
        {
            return false;
        }

        var actual = followerThere - followerHere;
        var scheduled = (ServiceDayStart(follower.Trip.ServiceDate).AddSeconds(followerHere)
            - ServiceDayStart(leader.Trip.ServiceDate).AddSeconds(leaderHere)).TotalSeconds;
        if (scheduled <= 0)
        {
            return false;
        }

        var pair = new PairKey(
            new VehicleKey(follower.Position.FeedId, follower.Position.VehicleId),
            new VehicleKey(leader.Position.FeedId, leader.Position.VehicleId),
            follower.Trip.TripId,
            leader.Trip.TripId,
            follower.Trip.ServiceDate,
            leader.Trip.ServiceDate);
        measurement = new PairMeasurement(pair, follower, leader, actual, scheduled);
        return true;
    }

    private Timeline? TimelineOf(Observation observation) =>
        _timelines.GetValue(
            observation.Timetable,
            _ => new TimelineHolder(Timeline.Build(observation.Trip.Stops!, observation.Timetable))).Timeline;

    private DateTimeOffset ServiceDayStart(DateOnly date)
    {
        var localNoon = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localNoon, _timeZone)).AddHours(-12);
    }

    private void SweepIfDue()
    {
        if (_lastSweepAt == default)
        {
            _lastSweepAt = _latestAt;
            return;
        }

        if (_latestAt - _lastSweepAt < _sweepInterval)
        {
            return;
        }

        _lastSweepAt = _latestAt;
        foreach (var lineKey in _lines.Keys.ToArray())
        {
            var line = _lines[lineKey];
            PurgeStale(lineKey, line, _latestAt);
            Recalculate(line, _latestAt, events: null, allowConfirmation: false);
            RemoveLineIfEmpty(lineKey, line);
        }
    }

    private void PurgeStale(LineKey lineKey, LineState line, DateTimeOffset at)
    {
        _staleVehicles.Clear();
        foreach (var (vehicleKey, observation) in line.Vehicles)
        {
            if (at - observation.Position.ObservedAt > _freshness)
            {
                _staleVehicles.Add(vehicleKey);
            }
        }

        foreach (var stale in _staleVehicles)
        {
            line.Vehicles.Remove(stale);
            if (_lineOfVehicle.GetValueOrDefault(stale) == lineKey)
            {
                _lineOfVehicle.Remove(stale);
            }
        }

        _staleVehicles.Clear();
    }

    private void RemoveLineIfEmpty(LineKey lineKey, LineState line)
    {
        if (line.Vehicles.Count == 0 && line.Episodes.Count == 0)
        {
            _lines.Remove(lineKey);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;

    private sealed class LineState
    {
        public Dictionary<VehicleKey, Observation> Vehicles { get; } = [];

        public Dictionary<PairKey, EpisodeState> Episodes { get; } = [];
    }

    private sealed class EpisodeState(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset? RecoveringSince { get; set; }

        public bool Active { get; set; }
    }

    private sealed class TimelineHolder(Timeline? timeline)
    {
        public Timeline? Timeline { get; } = timeline;
    }

    private sealed class ObservationComparer : IComparer<Observation>
    {
        public static readonly ObservationComparer Instance = new();

        public int Compare(Observation x, Observation y)
        {
            var along = x.AlongMetres.CompareTo(y.AlongMetres);
            if (along != 0)
            {
                return along;
            }

            var feed = string.Compare(x.Position.FeedId, y.Position.FeedId, StringComparison.Ordinal);
            return feed != 0
                ? feed
                : string.Compare(x.Position.VehicleId, y.Position.VehicleId, StringComparison.Ordinal);
        }
    }

    private sealed class Timeline(double[] distances, double[] seconds)
    {
        public static Timeline? Build(StopPattern pattern, StopTimetable timetable)
        {
            var distances = new List<double>();
            var seconds = new List<double>();
            foreach (var stop in pattern.Calls)
            {
                if (!timetable.TryFind(stop.Sequence, out var call)
                    || (call.ArrivalSeconds ?? call.DepartureSeconds) is not { } at)
                {
                    continue;
                }

                if (distances.Count > 0 && stop.AlongMetres <= distances[^1])
                {
                    if (stop.AlongMetres == distances[^1] && at >= seconds[^1])
                    {
                        seconds[^1] = at;
                    }

                    continue;
                }

                if (seconds.Count > 0 && at < seconds[^1])
                {
                    continue;
                }

                distances.Add(stop.AlongMetres);
                seconds.Add(at);
            }

            return distances.Count >= 2 ? new Timeline([.. distances], [.. seconds]) : null;
        }

        public bool TryAt(double distance, out double at)
        {
            at = 0;
            if (distance < distances[0] || distance > distances[^1])
            {
                return false;
            }

            var index = Array.BinarySearch(distances, distance);
            if (index >= 0)
            {
                at = seconds[index];
                return true;
            }

            var after = ~index;
            var before = after - 1;
            var fraction = (distance - distances[before]) / (distances[after] - distances[before]);
            at = seconds[before] + ((seconds[after] - seconds[before]) * fraction);
            return true;
        }
    }

    private readonly record struct LineKey(string FeedId, string RouteId, int DirectionId, StopPattern Pattern);

    private readonly record struct VehicleKey(string FeedId, string VehicleId);

    private readonly record struct PairKey(
        VehicleKey Follower,
        VehicleKey Leader,
        string FollowerTripId,
        string LeaderTripId,
        DateOnly FollowerServiceDate,
        DateOnly LeaderServiceDate)
    {
        public bool Contains(VehicleKey vehicle) => Follower == vehicle || Leader == vehicle;
    }

    private readonly record struct Observation(
        VehiclePosition Position,
        ScheduledTrip Trip,
        StopTimetable Timetable,
        double AlongMetres);

    private readonly record struct PairMeasurement(
        PairKey Pair,
        Observation Follower,
        Observation Leader,
        double ActualHeadwaySec,
        double ScheduledHeadwaySec)
    {
        public double Ratio => ActualHeadwaySec / ScheduledHeadwaySec;

        public BunchingEvent Event(DateTimeOffset startedAt, DateTimeOffset confirmedAt) => new(
            Follower.Position.FeedId,
            Follower.Trip.RouteId,
            Follower.Trip.DirectionId,
            Follower.Position.VehicleId,
            Leader.Position.VehicleId,
            Follower.Trip.TripId,
            Leader.Trip.TripId,
            Follower.Trip.ServiceDate,
            Leader.Trip.ServiceDate,
            startedAt.ToUniversalTime(),
            confirmedAt.ToUniversalTime(),
            ActualHeadwaySec,
            ScheduledHeadwaySec);
    }
}

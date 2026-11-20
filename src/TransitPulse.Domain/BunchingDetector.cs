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

    internal BunchingDetectorSnapshot Capture()
    {
        var lines = new BunchingLineSnapshot[_lines.Count];
        var lineIndex = 0;
        foreach (var (key, line) in _lines)
        {
            var vehicles = new BunchingObservationSnapshot[line.Vehicles.Count];
            var vehicleIndex = 0;
            foreach (var observation in line.Vehicles.Values)
            {
                vehicles[vehicleIndex++] = new BunchingObservationSnapshot(
                    observation.Position,
                    observation.Trip.TripId,
                    observation.Trip.ServiceDate,
                    observation.AlongMetres);
            }

            Array.Sort(vehicles, static (first, second) =>
            {
                var feed = string.CompareOrdinal(first.Position.FeedId, second.Position.FeedId);
                return feed != 0 ? feed : string.CompareOrdinal(first.Position.VehicleId, second.Position.VehicleId);
            });

            var episodes = new BunchingEpisodeSnapshot[line.Episodes.Count];
            var episodeIndex = 0;
            foreach (var (pair, episode) in line.Episodes)
            {
                episodes[episodeIndex++] = new BunchingEpisodeSnapshot(
                    pair.Follower.FeedId,
                    pair.Follower.VehicleId,
                    pair.Leader.FeedId,
                    pair.Leader.VehicleId,
                    pair.FollowerTripId,
                    pair.LeaderTripId,
                    pair.FollowerServiceDate,
                    pair.LeaderServiceDate,
                    episode.StartedAt,
                    episode.RecoveringSince,
                    episode.Active);
            }

            Array.Sort(episodes, static (first, second) => CompareEpisodes(first, second));
            var (patternTripId, patternServiceDate) = PatternIdentity(line);
            lines[lineIndex++] = new BunchingLineSnapshot(
                key.FeedId,
                key.RouteId,
                key.DirectionId,
                patternTripId,
                patternServiceDate,
                vehicles,
                episodes);
        }

        Array.Sort(lines, static (first, second) => CompareLines(first, second));
        return new BunchingDetectorSnapshot(_latestAt, _lastSweepAt, lines);
    }

    internal static BunchingDetector Restore(
        BunchingDetectorSnapshot snapshot,
        TripSchedule? schedule,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (snapshot.Lines is null
            || !ValidTime(snapshot.LatestAt, allowDefault: snapshot.Lines.Length == 0)
            || !ValidTime(snapshot.LastSweepAt, allowDefault: true)
            || snapshot.LastSweepAt != default && snapshot.LastSweepAt > snapshot.LatestAt)
        {
            throw InvalidSnapshot();
        }

        var detector = new BunchingDetector(timeZone)
        {
            _latestAt = snapshot.LatestAt,
            _lastSweepAt = snapshot.LastSweepAt,
        };
        foreach (var savedLine in snapshot.Lines)
        {
            if (savedLine.Vehicles is null
                || savedLine.Episodes is null
                || string.IsNullOrWhiteSpace(savedLine.FeedId)
                || string.IsNullOrWhiteSpace(savedLine.RouteId)
                || savedLine.DirectionId is < 0 or > 1
                || savedLine.Vehicles.Length == 0 && savedLine.Episodes.Length == 0)
            {
                throw InvalidSnapshot();
            }

            var patternTrip = Rebind(schedule, savedLine.PatternTripId, savedLine.PatternServiceDate);
            var pattern = RequireLine(patternTrip, savedLine, pattern: null, requireTimetable: false).Stops!;
            var lineKey = new LineKey(savedLine.FeedId, savedLine.RouteId, savedLine.DirectionId, pattern);
            var line = new LineState();
            if (!detector._lines.TryAdd(lineKey, line))
            {
                throw InvalidSnapshot();
            }

            foreach (var saved in savedLine.Vehicles)
            {
                ArgumentNullException.ThrowIfNull(saved.Position);
                if (saved.Position.FeedId != savedLine.FeedId
                    || !double.IsFinite(saved.AlongMetres)
                    || saved.AlongMetres < 0
                    || saved.Position.ObservedAt > snapshot.LatestAt)
                {
                    throw InvalidSnapshot();
                }

                var trip = RequireLine(
                    Rebind(schedule, saved.TripId, saved.ServiceDate), savedLine, pattern, requireTimetable: true);
                var vehicleKey = new VehicleKey(saved.Position.FeedId, saved.Position.VehicleId);
                var observation = new Observation(saved.Position, trip, trip.Timetable!, saved.AlongMetres);
                if (!line.Vehicles.TryAdd(vehicleKey, observation)
                    || !detector._lineOfVehicle.TryAdd(vehicleKey, lineKey))
                {
                    throw InvalidSnapshot();
                }
            }

            foreach (var saved in savedLine.Episodes)
            {
                ValidateEpisode(saved, savedLine, snapshot.LatestAt);
                RequireLine(
                    Rebind(schedule, saved.FollowerTripId, saved.FollowerServiceDate), savedLine, pattern, requireTimetable: false);
                RequireLine(
                    Rebind(schedule, saved.LeaderTripId, saved.LeaderServiceDate), savedLine, pattern, requireTimetable: false);
                var pair = new PairKey(
                    new VehicleKey(saved.FollowerFeedId, saved.FollowerVehicleId),
                    new VehicleKey(saved.LeaderFeedId, saved.LeaderVehicleId),
                    saved.FollowerTripId,
                    saved.LeaderTripId,
                    saved.FollowerServiceDate,
                    saved.LeaderServiceDate);
                if (!line.Episodes.TryAdd(pair, new EpisodeState(saved.StartedAt)
                {
                    RecoveringSince = saved.RecoveringSince,
                    Active = saved.Active,
                }))
                {
                    throw InvalidSnapshot();
                }
            }
        }

        return detector;
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

    private static (string TripId, DateOnly ServiceDate) PatternIdentity(LineState line)
    {
        string? tripId = null;
        var serviceDate = default(DateOnly);

        void Consider(string candidateId, DateOnly candidateDate)
        {
            var comparison = tripId is null ? -1 : string.CompareOrdinal(candidateId, tripId);
            if (tripId is null || comparison < 0 || comparison == 0 && candidateDate < serviceDate)
            {
                tripId = candidateId;
                serviceDate = candidateDate;
            }
        }

        foreach (var observation in line.Vehicles.Values)
        {
            Consider(observation.Trip.TripId, observation.Trip.ServiceDate);
        }

        foreach (var pair in line.Episodes.Keys)
        {
            Consider(pair.FollowerTripId, pair.FollowerServiceDate);
            Consider(pair.LeaderTripId, pair.LeaderServiceDate);
        }

        return tripId is not null
            ? (tripId, serviceDate)
            : throw new InvalidOperationException("A bunching line has neither vehicles nor episodes.");
    }

    private static ScheduledTrip Rebind(TripSchedule? schedule, string tripId, DateOnly serviceDate) =>
        schedule is not null && schedule.TryFindByIdentity(tripId, serviceDate, out var trip)
            ? trip!
            : throw new InvalidOperationException($"Trip {tripId} on {serviceDate} is absent from the current schedule.");

    private static ScheduledTrip RequireLine(
        ScheduledTrip trip,
        BunchingLineSnapshot line,
        StopPattern? pattern,
        bool requireTimetable)
    {
        if (trip.RouteId != line.RouteId
            || trip.DirectionId != line.DirectionId
            || trip.Stops is null
            || pattern is not null && !ReferenceEquals(trip.Stops, pattern)
            || requireTimetable && trip.Timetable is null)
        {
            throw new InvalidOperationException($"Trip {trip.TripId} on {trip.ServiceDate} is incompatible with its bunching line.");
        }

        return trip;
    }

    private static void ValidateEpisode(BunchingEpisodeSnapshot episode, BunchingLineSnapshot line, DateTimeOffset latestAt)
    {
        if (episode.FollowerFeedId != line.FeedId
            || episode.LeaderFeedId != line.FeedId
            || string.IsNullOrWhiteSpace(episode.FollowerVehicleId)
            || string.IsNullOrWhiteSpace(episode.LeaderVehicleId)
            || episode.FollowerVehicleId == episode.LeaderVehicleId
            || string.IsNullOrWhiteSpace(episode.FollowerTripId)
            || string.IsNullOrWhiteSpace(episode.LeaderTripId)
            || episode.FollowerServiceDate == default
            || episode.LeaderServiceDate == default
            || !ValidTime(episode.StartedAt, allowDefault: false)
            || episode.StartedAt > latestAt
            || !episode.Active && episode.RecoveringSince is not null
            || episode.RecoveringSince is { } recovering
                && (!ValidTime(recovering, allowDefault: false) || recovering < episode.StartedAt || recovering > latestAt))
        {
            throw InvalidSnapshot();
        }
    }

    private static bool ValidTime(DateTimeOffset value, bool allowDefault) =>
        allowDefault && value == default || value != default && value.Offset == TimeSpan.Zero;

    private static int CompareLines(BunchingLineSnapshot first, BunchingLineSnapshot second)
    {
        var comparison = string.CompareOrdinal(first.FeedId, second.FeedId);
        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.RouteId, second.RouteId);
        }

        if (comparison == 0)
        {
            comparison = first.DirectionId.CompareTo(second.DirectionId);
        }

        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.PatternTripId, second.PatternTripId);
        }

        return comparison != 0 ? comparison : first.PatternServiceDate.CompareTo(second.PatternServiceDate);
    }

    private static int CompareEpisodes(BunchingEpisodeSnapshot first, BunchingEpisodeSnapshot second)
    {
        var comparison = string.CompareOrdinal(first.FollowerFeedId, second.FollowerFeedId);
        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.FollowerVehicleId, second.FollowerVehicleId);
        }

        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.LeaderFeedId, second.LeaderFeedId);
        }

        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.LeaderVehicleId, second.LeaderVehicleId);
        }

        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.FollowerTripId, second.FollowerTripId);
        }

        if (comparison == 0)
        {
            comparison = string.CompareOrdinal(first.LeaderTripId, second.LeaderTripId);
        }

        if (comparison == 0)
        {
            comparison = first.FollowerServiceDate.CompareTo(second.FollowerServiceDate);
        }

        return comparison != 0 ? comparison : first.LeaderServiceDate.CompareTo(second.LeaderServiceDate);
    }

    private static ArgumentException InvalidSnapshot() =>
        new("The bunching detector snapshot is inconsistent.", "snapshot");

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

internal readonly record struct BunchingDetectorSnapshot(
    DateTimeOffset LatestAt,
    DateTimeOffset LastSweepAt,
    BunchingLineSnapshot[] Lines);

internal readonly record struct BunchingLineSnapshot(
    string FeedId,
    string RouteId,
    int DirectionId,
    string PatternTripId,
    DateOnly PatternServiceDate,
    BunchingObservationSnapshot[] Vehicles,
    BunchingEpisodeSnapshot[] Episodes);

internal readonly record struct BunchingObservationSnapshot(
    VehiclePosition Position,
    string TripId,
    DateOnly ServiceDate,
    double AlongMetres);

internal readonly record struct BunchingEpisodeSnapshot(
    string FollowerFeedId,
    string FollowerVehicleId,
    string LeaderFeedId,
    string LeaderVehicleId,
    string FollowerTripId,
    string LeaderTripId,
    DateOnly FollowerServiceDate,
    DateOnly LeaderServiceDate,
    DateTimeOffset StartedAt,
    DateTimeOffset? RecoveringSince,
    bool Active);

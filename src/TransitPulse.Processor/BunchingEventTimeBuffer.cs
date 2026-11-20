using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>
/// Restores bounded event-time order before positions from vehicle-keyed Kafka partitions reach the process-wide
/// bunching detector.
/// </summary>
/// <remarks>
/// Kafka orders one partition, not the process-wide stream. Each assigned partition therefore contributes a watermark;
/// the detector sees only observations older than the slowest active partition. A small allowance also absorbs timestamp
/// skew between different vehicles in one partition. A partition at its current end cannot hold the watermark back.
/// </remarks>
internal sealed class BunchingEventTimeBuffer
{
    internal static readonly TimeSpan AllowedLateness = TimeSpan.FromSeconds(5);

    private readonly BunchingDetector _detector;
    private readonly Dictionary<int, PartitionProgress> _partitions = [];
    private readonly PriorityQueue<PendingObservation, (long At, long Sequence)> _pending = new();
    private long _nextGeneration;
    private long _nextSequence;
    private DateTimeOffset _processedThrough;

    public BunchingEventTimeBuffer(TimeZoneInfo timeZone)
    {
        _detector = new BunchingDetector(timeZone);
    }

    private BunchingEventTimeBuffer(BunchingDetector detector)
    {
        _detector = detector;
    }

    internal BunchingEventTimeBufferSnapshot Capture()
    {
        var partitions = new BunchingPartitionProgressSnapshot[_partitions.Count];
        var partitionIndex = 0;
        foreach (var (partition, progress) in _partitions)
        {
            partitions[partitionIndex++] = new BunchingPartitionProgressSnapshot(
                partition,
                progress.Generation,
                progress.LatestAt,
                progress.HasObserved,
                progress.AtEnd);
        }

        Array.Sort(partitions, static (first, second) => first.Partition.CompareTo(second.Partition));

        var pending = new List<BunchingPendingObservationSnapshot>(_pending.Count);
        foreach (var item in _pending.UnorderedItems)
        {
            var observation = item.Element;
            if (!_partitions.TryGetValue(observation.Partition, out var progress)
                || progress.Generation != observation.Generation)
            {
                continue;
            }

            pending.Add(new BunchingPendingObservationSnapshot(
                observation.Partition,
                observation.Generation,
                observation.Position,
                observation.Trip.TripId,
                observation.Trip.ServiceDate,
                observation.AlongMetres,
                item.Priority.At,
                item.Priority.Sequence));
        }

        pending.Sort(static (first, second) =>
        {
            var at = first.At.CompareTo(second.At);
            return at != 0 ? at : first.Sequence.CompareTo(second.Sequence);
        });
        return new BunchingEventTimeBufferSnapshot(
            _nextGeneration,
            _nextSequence,
            _processedThrough,
            partitions,
            [.. pending],
            _detector.Capture());
    }

    internal static BunchingEventTimeBuffer Restore(
        BunchingEventTimeBufferSnapshot snapshot,
        TripSchedule? schedule,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (snapshot.Partitions is null
            || snapshot.Pending is null
            || snapshot.NextGeneration < 0
            || snapshot.NextSequence < 0
            || !ValidTime(snapshot.ProcessedThrough, allowDefault: true))
        {
            throw InvalidSnapshot();
        }

        var detector = BunchingDetector.Restore(snapshot.Detector, schedule, timeZone);
        var buffer = new BunchingEventTimeBuffer(detector)
        {
            _nextGeneration = snapshot.NextGeneration,
            _nextSequence = snapshot.NextSequence,
            _processedThrough = snapshot.ProcessedThrough,
        };
        foreach (var saved in snapshot.Partitions)
        {
            if (saved.Partition < 0
                || saved.Generation <= 0
                || saved.Generation > snapshot.NextGeneration
                || saved.HasObserved && !ValidTime(saved.LatestAt, allowDefault: false)
                || !saved.HasObserved && saved.LatestAt != default
                || !buffer._partitions.TryAdd(saved.Partition, new PartitionProgress(saved.Generation)
                {
                    LatestAt = saved.LatestAt,
                    HasObserved = saved.HasObserved,
                    AtEnd = saved.AtEnd,
                }))
            {
                throw InvalidSnapshot();
            }
        }

        var sequences = new HashSet<long>();
        foreach (var saved in snapshot.Pending)
        {
            ArgumentNullException.ThrowIfNull(saved.Position);
            if (!buffer._partitions.TryGetValue(saved.Partition, out var progress)
                || progress.Generation != saved.Generation
                || saved.At != saved.Position.ObservedAt.UtcTicks
                || saved.Sequence < 0
                || saved.Sequence >= snapshot.NextSequence
                || !sequences.Add(saved.Sequence)
                || !double.IsFinite(saved.AlongMetres)
                || saved.AlongMetres < 0)
            {
                throw InvalidSnapshot();
            }

            var trip = schedule is not null && schedule.TryFindByIdentity(saved.TripId, saved.ServiceDate, out var found)
                ? found!
                : throw new InvalidOperationException($"Trip {saved.TripId} on {saved.ServiceDate} is absent from the current schedule.");
            var pending = new PendingObservation(
                saved.Partition,
                saved.Generation,
                saved.Position,
                trip,
                saved.AlongMetres);
            buffer._pending.Enqueue(pending, (saved.At, saved.Sequence));
        }

        return buffer;
    }

    /// <summary>Adds newly assigned partitions. Until each has a position or reaches its current end, observations wait.</summary>
    public void Assign(IEnumerable<int> partitions)
    {
        foreach (var partition in partitions)
        {
            if (!_partitions.ContainsKey(partition))
            {
                _partitions.Add(partition, new PartitionProgress(++_nextGeneration));
            }
        }
    }

    /// <summary>Observes one accepted position and, when it is on-route, queues it for bunching detection.</summary>
    public void Observe(
        int partition,
        VehiclePosition position,
        DateTimeOffset receivedAt,
        ScheduledTrip? trip,
        double? alongMetres,
        ICollection<BunchingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(events);

        // One bad source clock must not advance the process watermark and suppress every normally timed vehicle.
        var latestAllowed = receivedAt >= DateTimeOffset.MaxValue - AllowedLateness
            ? DateTimeOffset.MaxValue
            : receivedAt + AllowedLateness;
        if (position.ObservedAt > latestAllowed)
        {
            return;
        }

        if (!_partitions.TryGetValue(partition, out var progress))
        {
            progress = new PartitionProgress(++_nextGeneration);
            _partitions.Add(partition, progress);
        }

        progress.AtEnd = false;
        progress.HasObserved = true;
        progress.LatestAt = Max(progress.LatestAt, position.ObservedAt);
        if (trip is not null && alongMetres is { } along)
        {
            var pending = new PendingObservation(partition, progress.Generation, position, trip, along);
            _pending.Enqueue(pending, (position.ObservedAt.UtcTicks, _nextSequence++));
        }

        Drain(events);
    }

    /// <summary>Marks a partition as read to its current high watermark, so an empty or idle partition cannot block output.</summary>
    public void ReachedEnd(int partition, ICollection<BunchingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_partitions.TryGetValue(partition, out var progress))
        {
            progress.AtEnd = true;
            Drain(events);
        }
    }

    /// <summary>Forgets pending observations and detector state owned by partitions that left this process.</summary>
    public void Remove(IEnumerable<int> partitions, IEnumerable<(string FeedId, string VehicleId)> vehicles)
    {
        foreach (var partition in partitions)
        {
            _partitions.Remove(partition);
        }

        foreach (var (feedId, vehicleId) in vehicles)
        {
            _detector.Remove(feedId, vehicleId);
        }
    }

    private void Drain(ICollection<BunchingEvent> events)
    {
        if (!TryWatermark(out var watermark))
        {
            return;
        }

        while (_pending.TryPeek(out _, out var priority) && priority.At <= watermark.UtcTicks)
        {
            var pending = _pending.Dequeue();
            if (!_partitions.TryGetValue(pending.Partition, out var progress)
                || progress.Generation != pending.Generation
                || pending.Position.ObservedAt < _processedThrough)
            {
                continue;
            }

            _detector.Advance(pending.Position, pending.Trip, pending.AlongMetres, events);
            _processedThrough = Max(_processedThrough, pending.Position.ObservedAt);
        }
    }

    private bool TryWatermark(out DateTimeOffset watermark)
    {
        watermark = DateTimeOffset.MaxValue;
        var hasActive = false;
        foreach (var progress in _partitions.Values)
        {
            if (!progress.HasObserved && !progress.AtEnd)
            {
                return false;
            }

            if (progress.AtEnd)
            {
                continue;
            }

            hasActive = true;
            var partitionWatermark = progress.LatestAt <= DateTimeOffset.MinValue + AllowedLateness
                ? DateTimeOffset.MinValue
                : progress.LatestAt - AllowedLateness;
            if (partitionWatermark < watermark)
            {
                watermark = partitionWatermark;
            }
        }

        if (!hasActive)
        {
            watermark = DateTimeOffset.MaxValue;
        }

        return true;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;

    private static bool ValidTime(DateTimeOffset value, bool allowDefault) =>
        allowDefault && value == default || value != default && value.Offset == TimeSpan.Zero;

    private static ArgumentException InvalidSnapshot() =>
        new("The bunching event-time buffer snapshot is inconsistent.", "snapshot");

    private sealed class PartitionProgress(long generation)
    {
        public long Generation { get; } = generation;

        public DateTimeOffset LatestAt { get; set; }

        public bool HasObserved { get; set; }

        public bool AtEnd { get; set; }
    }

    private readonly record struct PendingObservation(
        int Partition,
        long Generation,
        VehiclePosition Position,
        ScheduledTrip Trip,
        double AlongMetres);
}

internal readonly record struct BunchingEventTimeBufferSnapshot(
    long NextGeneration,
    long NextSequence,
    DateTimeOffset ProcessedThrough,
    BunchingPartitionProgressSnapshot[] Partitions,
    BunchingPendingObservationSnapshot[] Pending,
    BunchingDetectorSnapshot Detector);

internal readonly record struct BunchingPartitionProgressSnapshot(
    int Partition,
    long Generation,
    DateTimeOffset LatestAt,
    bool HasObserved,
    bool AtEnd);

internal readonly record struct BunchingPendingObservationSnapshot(
    int Partition,
    long Generation,
    VehiclePosition Position,
    string TripId,
    DateOnly ServiceDate,
    double AlongMetres,
    long At,
    long Sequence);

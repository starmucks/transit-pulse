using Confluent.Kafka;
using Microsoft.Extensions.Options;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Kafka;

/// <summary>
/// The Processor's main loop: reads positions, drops the implausible ones and publishes the rest with the stop events they
/// complete. Delivery is
/// at-least-once: a partition's offset is committed only after everything read before it was delivered, so a crash repeats
/// recent messages and loses none. One loop serves all partitions of the process (ADR-0010).
/// </summary>
/// <param name="options">Broker and consumer settings.</param>
/// <param name="schedule">Where accepted positions look up their trips.</param>
/// <param name="checkpoints">Durable state saved after output delivery and before offset commit.</param>
/// <param name="reader">Decodes one <c>positions.raw</c> record before the partition state sees it.</param>
/// <param name="commitBarrier">Fails the run in the checkpoint→commit window when a test asks for a crash.</param>
/// <param name="metrics">Counters and lag.</param>
/// <param name="timeProvider">Clock for the commit interval and the lag.</param>
/// <param name="logger">Log for broker errors, dropped messages and progress.</param>
internal sealed partial class PositionConsumer(
    IOptions<ProcessorKafkaOptions> options,
    CurrentTripSchedule schedule,
    IProcessorCheckpointStore checkpoints,
    IPositionMessageReader reader,
    ICommitBarrier commitBarrier,
    ProcessorMetrics metrics,
    TimeProvider timeProvider,
    ILogger<PositionConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan _lagInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _progressInterval = TimeSpan.FromMinutes(1);

    // Bounds how late the commit timer and a stop request are noticed on an idle topic.
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _flushTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _queueFullBackoff = TimeSpan.FromMilliseconds(100);

    private readonly ProcessorKafkaOptions _options = options.Value;
    private BunchingEventTimeBuffer _bunching = new(schedule.TimeZone);

    // Touched only by the loop.
    private readonly Dictionary<int, PartitionState> _partitions = [];
    private readonly Dictionary<int, HashSet<(string FeedId, string VehicleId)>> _bunchingVehicles = [];
    private readonly List<BunchingEvent> _bunchingEvents = [];
    private long _unreadable;
    private TopicPartitionOffset? _firstUnreadable;
    private bool _abandoned;

    // Set on the clients' threads.
    private long _failedDeliveries;
    private Error? _fatalError;

    /// <inheritdoc />
    /// <remarks>On its own thread: the client's calls block, and the loop holds its thread for the life of the process.</remarks>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken stopping)
    {
        using var producer = BuildProducer();
        using var consumer = BuildConsumer(producer);
        consumer.Subscribe(_options.InputTopic);

        try
        {
            var start = timeProvider.GetUtcNow();
            var nextCommit = start + _options.CommitInterval;
            var nextLag = start + _lagInterval;
            var nextProgress = start + _progressInterval;
            while (!stopping.IsCancellationRequested)
            {
                ThrowIfFailed();
                if (TryConsume(consumer) is { } result)
                {
                    if (result.IsPartitionEOF)
                    {
                        HandlePartitionEnd(result.Partition.Value, producer, stopping);
                    }
                    else
                    {
                        Handle(result, producer, stopping);
                    }
                }

                var now = timeProvider.GetUtcNow();
                if (now >= nextCommit)
                {
                    CommitProcessed(consumer, producer, _partitions.Keys);
                    nextCommit = timeProvider.GetUtcNow() + _options.CommitInterval;
                }

                if (now >= nextLag)
                {
                    ReportLag(consumer);
                    nextLag = now + _lagInterval;
                }

                if (now >= nextProgress)
                {
                    LogTotals();
                    nextProgress = now + _progressInterval;
                }
            }

            CommitProcessed(consumer, producer, _partitions.Keys);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopped while the message in hand waited for room in the producer's buffer. Its offset has moved, so nothing
            // may be committed, including by Close below: the next start repeats everything since the last commit.
            _abandoned = true;
        }
        catch
        {
            // Leave the group promptly after a failed run, but do not let the revoke callback commit the failed boundary.
            _abandoned = true;
            throw;
        }
        finally
        {
            // Also after a failure: the totals of a failed run are what explains it.
            LogTotals();
            // Close sends LeaveGroup. Dispose alone can leave the dead member assigned until its session times out.
            consumer.Close();
        }
    }

    private ConsumeResult<string, byte[]>? TryConsume(IConsumer<string, byte[]> consumer)
    {
        try
        {
            return consumer.Consume(_pollTimeout);
        }
        catch (ConsumeException ex) when (!ex.Error.IsFatal && ex.InnerException is null)
        {
            LogConsumeError(ex.Error.Code, ex.Error.Reason);
            return null;
        }
    }

    private void Handle(ConsumeResult<string, byte[]> result, IProducer<string, byte[]> producer, CancellationToken stopping)
    {
        var partition = result.Partition.Value;
        if (!_partitions.TryGetValue(partition, out var state))
        {
            state = new PartitionState(schedule);
            _partitions.Add(partition, state);
        }

        ProcessedMessage processed;
        try
        {
            var position = ReadWithAttempts(result.Message.Key, result.Message.Value);
            processed = state.ProcessParsed(
                result.Offset.Value,
                result.Message.Key
                ?? throw new InvalidOperationException("A decoded position has no Kafka key."),
                position);
        }
        catch (PositionMessageException ex)
        {
            // Decode failures are deterministic for the same bytes; the attempts still satisfy the stage contract and
            // leave room for a reader that may fail transiently later.
            PublishDeadLetter(result, ex, producer, stopping);
            processed = state.RejectUnreadable(result.Offset.Value);
        }

        _bunchingEvents.Clear();
        metrics.Message(processed.Verdict, LagSeconds(result.Message.Timestamp));
        if (processed.Trip is { } found)
        {
            metrics.Trip(found);
        }

        if (processed.Route is { } placed)
        {
            metrics.Route(placed);
        }

        if (processed.Delay is { } delay)
        {
            metrics.Delay(delay);
        }

        if (processed.Verdict == MessageVerdict.Accepted)
        {
            var position = processed.Position
                ?? throw new InvalidOperationException("An accepted position did not produce an enriched value.");
            Publish(_options.OutputTopic, ForOutput(result.Message, position), producer, stopping);
            var receivedAt = ReceivedAt(result.Message.Timestamp);
            if (processed is { Run: { } run, Route: { Status: RouteMatchStatus.OnRoute } route })
            {
                if (!_bunchingVehicles.TryGetValue(partition, out var vehicles))
                {
                    vehicles = [];
                    _bunchingVehicles.Add(partition, vehicles);
                }

                vehicles.Add((position.Position.FeedId, position.Position.VehicleId));
                _bunching.Observe(partition, position.Position, receivedAt, run, route.AlongMetres, _bunchingEvents);
            }
            else
            {
                _bunching.Observe(partition, position.Position, receivedAt, null, null, _bunchingEvents);
            }
        }
        else if (processed.Verdict == MessageVerdict.Unreadable)
        {
            _unreadable++;
            _firstUnreadable ??= result.TopicPartitionOffset;
        }

        // Through the same producer: the flush before a commit covers the events too, so a crash repeats them, never loses them.
        foreach (var stopEvent in state.StopEvents)
        {
            metrics.StopEvent(stopEvent.Kind);
            var message = new Message<string, byte[]> { Key = StopEventMessage.Key(stopEvent), Value = StopEventMessage.Value(stopEvent) };
            Publish(_options.StopEventsTopic, message, producer, stopping);
        }

        PublishBunching(producer, stopping);
    }

    private VehiclePosition ReadWithAttempts(string? key, ReadOnlySpan<byte> value)
    {
        PositionMessageException? last = null;
        for (var attempt = 1; attempt <= _options.ProcessingAttempts; attempt++)
        {
            try
            {
                return reader.Read(key, value);
            }
            catch (PositionMessageException ex)
            {
                last = ex;
            }
        }

        if (last is null)
        {
            throw new InvalidOperationException("Position decoding failed without an error.");
        }

        throw last;
    }

    private void PublishDeadLetter(
        ConsumeResult<string, byte[]> result,
        PositionMessageException error,
        IProducer<string, byte[]> producer,
        CancellationToken stopping)
    {
        var deadLetter = new RawPositionDeadLetter(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            result.Message.Key,
            result.Message.Value,
            _options.ProcessingAttempts,
            error.ErrorType,
            error.Message);
        Publish(
            _options.DeadLetterTopic,
            new Message<string, byte[]>
            {
                Key = RawPositionDeadLetterMessage.Key(deadLetter),
                Value = RawPositionDeadLetterMessage.Value(deadLetter),
            },
            producer,
            stopping);
    }

    private void HandlePartitionEnd(int partition, IProducer<string, byte[]> producer, CancellationToken stopping)
    {
        _bunchingEvents.Clear();
        _bunching.ReachedEnd(partition, _bunchingEvents);
        PublishBunching(producer, stopping);
    }

    private void PublishBunching(IProducer<string, byte[]> producer, CancellationToken stopping)
    {
        foreach (var bunching in _bunchingEvents)
        {
            metrics.Bunching(bunching);
            var message = new Message<string, byte[]>
            {
                Key = AnomalyEventMessage.Key(bunching),
                Value = AnomalyEventMessage.Value(bunching),
            };
            Publish(_options.AnomalyEventsTopic, message, producer, stopping);
        }
    }

    // The client accepts only a writer's time or none: passing on a time the broker stamped makes every Produce throw.
    private static Message<string, byte[]> ForOutput(Message<string, byte[]> input, EnrichedPosition position) => new()
    {
        Key = input.Key,
        Value = EnrichedPositionMessage.Value(position),
        Timestamp = input.Timestamp.Type == TimestampType.CreateTime ? input.Timestamp : Timestamp.Default,
        Headers = input.Headers,
    };

    private double? LagSeconds(Timestamp written) => written.Type == TimestampType.NotAvailable
        ? null
        : (timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - written.UnixTimestampMs) / 1000.0;

    private DateTimeOffset ReceivedAt(Timestamp written)
    {
        var now = timeProvider.GetUtcNow();
        if (written.Type == TimestampType.NotAvailable)
        {
            return now;
        }

        var brokerAt = DateTimeOffset.FromUnixTimeMilliseconds(written.UnixTimestampMs);
        return brokerAt < now ? brokerAt : now;
    }

    // The high watermark comes from the client's last fetch, so this asks the broker nothing and cannot hold up the loop.
    // A partition with no message read yet has no position to measure from and is left out.
    private void ReportLag(IConsumer<string, byte[]> consumer)
    {
        var lag = new List<(int Partition, long Lag)>(_partitions.Count);
        foreach (var (partition, state) in _partitions)
        {
            var high = consumer.GetWatermarkOffsets(new TopicPartition(_options.InputTopic, partition)).High;
            if (!high.IsSpecial && state.NextOffset is { } next)
            {
                lag.Add((partition, Math.Max(high.Value - next, 0)));
            }
        }

        metrics.SetLag(lag);
    }

    private void LogTotals()
    {
        var (accepted, outOfOrder) = (metrics.Total(MessageVerdict.Accepted), metrics.Total(MessageVerdict.OutOfOrder));
        var (jumps, unreadable) = (metrics.Total(MessageVerdict.ImpossibleJump), metrics.Total(MessageVerdict.Unreadable));
        var (tripsFound, tripLookups) = (metrics.Total(TripResolution.Found), metrics.TripLookups);
        var (onRoute, routeMatches) = (metrics.Total(RouteMatchStatus.OnRoute), metrics.RouteMatches);
        var (arrivals, departures, passes) = (
            metrics.Total(StopEventKind.Arrival), metrics.Total(StopEventKind.Departure), metrics.Total(StopEventKind.Passed));
        LogProgress(
            accepted,
            outOfOrder,
            jumps,
            unreadable,
            tripsFound,
            tripLookups,
            onRoute,
            routeMatches,
            arrivals,
            departures,
            passes,
            metrics.BunchingTotal);
    }

    // Blocks while the producer's buffer is full, which stops the reading too: the broker being down holds the input back
    // instead of filling memory.
    private void Publish(string topic, Message<string, byte[]> message, IProducer<string, byte[]> producer, CancellationToken stopping)
    {
        while (true)
        {
            try
            {
                producer.Produce(topic, message, OnDelivered);
                return;
            }
            catch (ProduceException<string, byte[]> ex) when (ex.Error.Code == ErrorCode.Local_QueueFull)
            {
                stopping.WaitHandle.WaitOne(_queueFullBackoff);
                stopping.ThrowIfCancellationRequested();
                ThrowIfFailed();
            }
        }
    }

    private void CommitProcessed(IConsumer<string, byte[]> consumer, IProducer<string, byte[]> producer, IEnumerable<int> partitions)
    {
        LogUnreadableSinceLastCommit();

        // Everything read so far was published on this thread, so a complete flush covers all the offsets below.
        var undelivered = producer.Flush(_flushTimeout);
        ThrowIfFailed();
        if (undelivered > 0)
        {
            LogFlushIncomplete(undelivered, _flushTimeout);
            return;
        }

        // Never replace the last useful checkpoint after this instance has lost or revoked everything.
        if (_partitions.Count == 0)
        {
            return;
        }

        checkpoints.Save(ProcessorCheckpoint.Capture(_partitions, _bunching, _bunchingVehicles));
        // Crash window for ADR-0014 tests: checkpoint is durable, Kafka offsets are not yet committed.
        commitBarrier.AfterCheckpointSaved();

        List<TopicPartitionOffset> offsets =
        [
            .. partitions
                .Where(partition => _partitions.TryGetValue(partition, out var state) && state.HasUncommitted)
                .Select(partition => new TopicPartitionOffset(_options.InputTopic, partition, _partitions[partition].NextOffset!.Value)),
        ];
        if (offsets.Count == 0)
        {
            return;
        }

        try
        {
            consumer.Commit(offsets);
        }
        catch (KafkaException ex) when (!ex.Error.IsFatal)
        {
            // Nothing is lost: the offsets stay uncommitted and go with the next commit.
            LogCommitFailed(ex.Error.Code, ex.Error.Reason);
            return;
        }

        foreach (var offset in offsets)
        {
            _partitions[offset.Partition.Value].MarkCommitted(offset.Offset.Value);
        }
    }

    // Restores checkpoint state for partitions that are not already live. A missing checkpoint after the group has
    // committed offsets is fail-closed (ADR-0014): silent empty state would skip stop and bunching history.
    private IEnumerable<TopicPartitionOffset> OnAssigned(IConsumer<string, byte[]> consumer, List<TopicPartition> assigned)
    {
        var partitions = assigned.Select(partition => partition.Partition.Value).ToList();
        var newcomers = partitions.Where(partition => !_partitions.ContainsKey(partition)).ToList();
        if (newcomers.Count > 0)
        {
            RestoreNewcomers(consumer, assigned, partitions, newcomers);
        }

        _bunching.Assign(partitions);
        LogAssigned(partitions);
        return
        [
            .. assigned.Select(partition => new TopicPartitionOffset(
                partition,
                _partitions.TryGetValue(partition.Partition.Value, out var state) && state.NextOffset is { } next
                    ? new Offset(next)
                    : Offset.Unset)),
        ];
    }

    private void RestoreNewcomers(
        IConsumer<string, byte[]> consumer,
        List<TopicPartition> assigned,
        List<int> ownedPartitions,
        List<int> newcomers)
    {
        var saved = checkpoints.Load();
        if (saved is { } snapshot)
        {
            var owned = new HashSet<int>(_partitions.Keys);
            owned.UnionWith(ownedPartitions);
            var restored = ProcessorCheckpoint.RestoreOwned(snapshot, schedule, owned);
            if (_partitions.Count == 0)
            {
                foreach (var (partition, state) in restored.Partitions)
                {
                    _partitions.Add(partition, state);
                }

                _bunching = restored.Bunching;
                foreach (var (partition, vehicles) in restored.BunchingVehicles)
                {
                    _bunchingVehicles.Add(partition, vehicles);
                }
            }

            // Cold start or cooperative re-assign: refill only newcomers still missing after the restore above.
            foreach (var partition in newcomers)
            {
                if (_partitions.ContainsKey(partition))
                {
                    continue;
                }

                if (!restored.Partitions.TryGetValue(partition, out var state))
                {
                    if (HasCommittedProgress(consumer, assigned.Where(topic => topic.Partition.Value == partition)))
                    {
                        throw new InvalidOperationException(
                            $"The Processor checkpoint has no state for partition {partition} after committed offsets.");
                    }

                    continue;
                }

                _partitions.Add(partition, state);
                if (restored.BunchingVehicles.TryGetValue(partition, out var vehicles))
                {
                    _bunchingVehicles.Add(partition, vehicles);
                }
            }

            return;
        }

        var newcomerTopics = assigned.Where(topic => newcomers.Contains(topic.Partition.Value)).ToList();
        if (HasCommittedProgress(consumer, newcomerTopics))
        {
            throw new InvalidOperationException(
                "The Processor checkpoint is missing after the consumer group has committed offsets.");
        }
    }

    private static bool HasCommittedProgress(IConsumer<string, byte[]> consumer, IEnumerable<TopicPartition> partitions)
    {
        var list = partitions as IList<TopicPartition> ?? partitions.ToList();
        if (list.Count == 0)
        {
            return false;
        }

        // Committed offset is the next message to read: > 0 means at least one record was finished.
        return consumer.Committed(list, TimeSpan.FromSeconds(10))
            .Any(committed => !committed.Offset.IsSpecial && committed.Offset.Value > 0);
    }

    // Runs on the loop's thread, inside Consume or Close. The new owner resumes from the commit made here, and without it
    // would repeat everything since the last timed commit.
    private void OnRevoked(IConsumer<string, byte[]> consumer, IProducer<string, byte[]> producer, List<TopicPartitionOffset> revoked)
    {
        var partitions = revoked.Select(partition => partition.Partition.Value).ToList();
        LogRevoked(partitions);
        if (!_abandoned)
        {
            CommitProcessed(consumer, producer, partitions);
        }

        Forget(partitions);
    }

    // The partitions already belong to another consumer: a commit would be refused, and the new owner repeats them.
    private void OnLost(List<TopicPartitionOffset> lost)
    {
        var partitions = lost.Select(partition => partition.Partition.Value).ToList();
        LogLost(partitions);
        Forget(partitions);
    }

    private void Forget(List<int> partitions)
    {
        var forgotten = new List<(string FeedId, string VehicleId)>();
        foreach (var partition in partitions)
        {
            _partitions.Remove(partition);
            if (_bunchingVehicles.Remove(partition, out var vehicles))
            {
                forgotten.AddRange(vehicles);
            }
        }

        _bunching.Remove(partitions, forgotten);
    }

    // A message the broker never accepted cannot be skipped: committing past it would lose it. Stopping the process
    // repeats everything since the last commit, and the service manager restarts it.
    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _fatalError) is { } fatal)
        {
            throw new KafkaException(fatal);
        }

        var failed = Interlocked.Read(ref _failedDeliveries);
        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"{failed} messages were not delivered to {_options.OutputTopic}, {_options.StopEventsTopic}, {_options.AnomalyEventsTopic}, or {_options.DeadLetterTopic}.");
        }
    }

    private void LogUnreadableSinceLastCommit()
    {
        if (_unreadable > 0)
        {
            LogUnreadable(_unreadable, _firstUnreadable!.ToString());
            _unreadable = 0;
            _firstUnreadable = null;
        }
    }

    private IProducer<string, byte[]> BuildProducer() => new ProducerBuilder<string, byte[]>(new ProducerConfig
    {
        BootstrapServers = _options.BootstrapServers,
        ClientId = "transitpulse-processor",
        // Retries cannot duplicate or reorder messages within a partition; implies acks=all.
        EnableIdempotence = true,
        LingerMs = 20,
        CompressionType = CompressionType.Lz4,
        // The Ingestor's partitioner: a vehicle keeps its partition number from topic to topic.
        Partitioner = Partitioner.Murmur2Random,
    }).SetErrorHandler((_, error) => OnError(error)).Build();

    private IConsumer<string, byte[]> BuildConsumer(IProducer<string, byte[]> producer) => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
    {
        BootstrapServers = _options.BootstrapServers,
        ClientId = "transitpulse-processor",
        GroupId = _options.GroupId,
        // Committed by CommitProcessed: the automatic commit does not know what has been delivered.
        EnableAutoCommit = false,
        // A partition with no commit is read from its start: positions published before the first start are not lost.
        AutoOffsetReset = AutoOffsetReset.Earliest,
        // Empty or caught-up partitions must not hold the event-time watermark back.
        EnablePartitionEof = true,
        // Only the partitions changing owner stop: the others keep their state through a rebalance.
        PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
    })
        .SetErrorHandler((_, error) => OnError(error))
        .SetPartitionsAssignedHandler((consumer, assigned) => OnAssigned(consumer, assigned))
        .SetPartitionsRevokedHandler((consumer, revoked) => OnRevoked(consumer, producer, revoked))
        .SetPartitionsLostHandler((_, lost) => OnLost(lost))
        .Build();

    private void OnDelivered(DeliveryReport<string, byte[]> report)
    {
        if (report.Error.IsError)
        {
            // Logged once: with the broker gone, thousands fail together and the loop stops at its next check.
            if (Interlocked.Increment(ref _failedDeliveries) == 1)
            {
                LogDeliveryFailed(report.Error.Reason);
            }
        }
    }

    private void OnError(Error error)
    {
        if (error.IsFatal)
        {
            Volatile.Write(ref _fatalError, error);
        }

        // Transient errors (broker down, connection reset) are retried by the client itself.
        LogBrokerError(error.Code, error.Reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Since the start: {Accepted} accepted, {OutOfOrder} out of order, {Jumps} impossible jumps, {Unreadable} unreadable; trips found for {TripsFound} of {TripLookups}; on the trip's line {OnRoute} of {RouteMatches}; stop events: {Arrivals} arrivals, {Departures} departures, {Passes} passes; {Bunching} bunching episodes")]
    private partial void LogProgress(
        long accepted, long outOfOrder, long jumps, long unreadable, long tripsFound, long tripLookups, long onRoute, long routeMatches,
        long arrivals, long departures, long passes, long bunching);

    [LoggerMessage(Level = LogLevel.Information, Message = "Partitions assigned: {Partitions}")]
    private partial void LogAssigned(List<int> partitions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Partitions revoked: {Partitions}")]
    private partial void LogRevoked(List<int> partitions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Partitions lost: {Partitions}")]
    private partial void LogLost(List<int> partitions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read from the broker: {Code}: {Reason}")]
    private partial void LogConsumeError(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {Count} unreadable messages since the last commit; the first: {First}")]
    private partial void LogUnreadable(long count, string first);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Undelivered} messages were still undelivered after {Timeout}; not committing yet")]
    private partial void LogFlushIncomplete(int undelivered, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commit failed, will retry: {Code}: {Reason}")]
    private partial void LogCommitFailed(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A message was not delivered: {Reason}")]
    private partial void LogDeliveryFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker error {Code}: {Reason}")]
    private partial void LogBrokerError(ErrorCode code, string reason);
}

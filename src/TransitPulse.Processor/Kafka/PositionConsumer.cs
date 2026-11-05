using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace TransitPulse.Processor.Kafka;

/// <summary>
/// The Processor's main loop: reads positions, drops the implausible ones and publishes the rest. Delivery is
/// at-least-once: a partition's offset is committed only after everything read before it was delivered, so a crash repeats
/// recent messages and loses none. One loop serves all partitions of the process (see the stage 2 specification).
/// </summary>
/// <param name="options">Broker and consumer settings.</param>
/// <param name="timeProvider">Clock for the commit interval.</param>
/// <param name="logger">Log for broker errors and dropped messages.</param>
internal sealed partial class PositionConsumer(IOptions<ProcessorKafkaOptions> options, TimeProvider timeProvider, ILogger<PositionConsumer> logger)
    : BackgroundService
{
    // Bounds how late the commit timer and a stop request are noticed on an idle topic.
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _flushTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _queueFullBackoff = TimeSpan.FromMilliseconds(100);

    private readonly ProcessorKafkaOptions _options = options.Value;

    // Touched only by the loop.
    private readonly Dictionary<int, PartitionState> _partitions = [];
    private long _unreadable;
    private TopicPartitionOffset? _firstUnreadable;

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
        using var consumer = BuildConsumer();
        consumer.Subscribe(_options.InputTopic);

        try
        {
            var nextCommit = timeProvider.GetUtcNow() + _options.CommitInterval;
            while (!stopping.IsCancellationRequested)
            {
                ThrowIfFailed();
                if (TryConsume(consumer) is { } result)
                {
                    Handle(result, producer, stopping);
                }

                if (timeProvider.GetUtcNow() >= nextCommit)
                {
                    CommitProcessed(consumer, producer);
                    nextCommit = timeProvider.GetUtcNow() + _options.CommitInterval;
                }
            }

            CommitProcessed(consumer, producer);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopped while the message in hand waited for room in the producer's buffer. Its offset has moved, so this
            // must not commit: the next start repeats everything since the last commit.
        }

        // Leaves the group at once, so the partitions move now rather than after the session times out.
        consumer.Close();
    }

    private ConsumeResult<string, byte[]>? TryConsume(IConsumer<string, byte[]> consumer)
    {
        try
        {
            return consumer.Consume(_pollTimeout);
        }
        catch (ConsumeException ex) when (!ex.Error.IsFatal)
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
            state = new PartitionState();
            _partitions.Add(partition, state);
        }

        var verdict = state.Process(result.Offset.Value, result.Message.Key, result.Message.Value);
        if (verdict == MessageVerdict.Accepted)
        {
            Publish(result.Message, producer, stopping);
        }
        else if (verdict == MessageVerdict.Unreadable)
        {
            _unreadable++;
            _firstUnreadable ??= result.TopicPartitionOffset;
        }
    }

    // Blocks while the producer's buffer is full, which stops the reading too: the broker being down holds the input back
    // instead of filling memory.
    private void Publish(Message<string, byte[]> message, IProducer<string, byte[]> producer, CancellationToken stopping)
    {
        while (true)
        {
            try
            {
                producer.Produce(_options.OutputTopic, message, OnDelivered);
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

    private void CommitProcessed(IConsumer<string, byte[]> consumer, IProducer<string, byte[]> producer)
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

        List<TopicPartitionOffset> offsets =
        [
            .. _partitions
                .Where(partition => partition.Value.HasUncommitted)
                .Select(partition => new TopicPartitionOffset(_options.InputTopic, partition.Key, partition.Value.NextOffset!.Value)),
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
            throw new InvalidOperationException($"{failed} positions were not delivered to {_options.OutputTopic}.");
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

    private IConsumer<string, byte[]> BuildConsumer() => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
    {
        BootstrapServers = _options.BootstrapServers,
        ClientId = "transitpulse-processor",
        GroupId = _options.GroupId,
        // Committed by CommitProcessed: the automatic commit does not know what has been delivered.
        EnableAutoCommit = false,
        // A partition with no commit is read from its start: positions published before the first start are not lost.
        AutoOffsetReset = AutoOffsetReset.Earliest,
    }).SetErrorHandler((_, error) => OnError(error)).Build();

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read from the broker: {Code}: {Reason}")]
    private partial void LogConsumeError(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {Count} unreadable messages since the last commit; the first: {First}")]
    private partial void LogUnreadable(long count, string first);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Undelivered} positions were still undelivered after {Timeout}; not committing yet")]
    private partial void LogFlushIncomplete(int undelivered, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commit failed, will retry: {Code}: {Reason}")]
    private partial void LogCommitFailed(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A position was not delivered: {Reason}")]
    private partial void LogDeliveryFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker error {Code}: {Reason}")]
    private partial void LogBrokerError(ErrorCode code, string reason);
}

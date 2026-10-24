using Confluent.Kafka;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;

namespace TransitPulse.Ingestor.Kafka;

/// <summary>
/// Publishes positions to a Kafka topic (<c>positions.raw</c>) with an idempotent, batching producer.
/// <see cref="PublishAsync"/> only enqueues into the client's local buffer; delivery is reported later on the client's
/// own thread. When that buffer is full it waits, so the pressure reaches <see cref="NormalizingSink"/>, which drops
/// records from publishing (ADR-0005) instead of this sink failing every message.
/// </summary>
internal sealed partial class KafkaSink : IPositionSink, IDisposable
{
    /// <summary>Pause before retrying a message the full local buffer refused.</summary>
    internal static readonly TimeSpan QueueFullBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>How long shutdown waits for buffered messages to be delivered.</summary>
    internal static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    private readonly IProducer<string, byte[]> _producer;
    private readonly string _topic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KafkaSink> _logger;
    private readonly Action<DeliveryReport<string, byte[]>> _onDelivered;

    // Delivery reports and errors arrive on the client's thread: these fields are shared with it.
    private long _failingDeliveries;
    private long _failedDeliveries;
    private int _fatal;

    // Touched only by the publishing loop.
    private long _droppedAfterFatal;

    /// <summary>Creates the producer; it connects lazily, so a missing broker does not fail construction.</summary>
    /// <param name="config">Connection settings (at least <c>BootstrapServers</c>); delivery settings are fixed here.</param>
    /// <param name="topic">Topic to publish to.</param>
    /// <param name="timeProvider">Clock for the back-off while the local buffer is full.</param>
    /// <param name="logger">Log for delivery and broker errors.</param>
    public KafkaSink(ProducerConfig config, string topic, TimeProvider timeProvider, ILogger<KafkaSink> logger)
    {
        _topic = topic;
        _timeProvider = timeProvider;
        _logger = logger;
        _onDelivered = OnDelivered;

        // A copy, so the caller's settings stay untouched.
        var settings = new ProducerConfig(config.ToDictionary(setting => setting.Key, setting => setting.Value))
        {
            // Retries cannot duplicate or reorder messages within a partition; implies acks=all.
            EnableIdempotence = true,
            // ~20 messages per batch at ~1 000/s, for 20 ms of extra latency.
            LingerMs = 20,
            CompressionType = CompressionType.Lz4,
            // The Java client's key hash: the same key lands in the same partition whichever client writes it.
            Partitioner = Partitioner.Murmur2Random,
        };
        _producer = new ProducerBuilder<string, byte[]>(settings).SetErrorHandler(OnError).Build();
    }

    /// <summary>Messages the broker never acknowledged since the sink was created.</summary>
    public long FailedDeliveries => Interlocked.Read(ref _failedDeliveries);

    /// <inheritdoc />
    public async ValueTask PublishAsync(IReadOnlyList<VehiclePosition> positions, CancellationToken cancellationToken)
    {
        // After a fatal error every Produce would throw; the loop would log each one.
        if (Volatile.Read(ref _fatal) == 1)
        {
            _droppedAfterFatal += positions.Count;
            return;
        }

        foreach (var position in positions)
        {
            var message = new Message<string, byte[]> { Key = PositionMessage.Key(position), Value = PositionMessage.Value(position) };
            while (!TryProduce(message))
            {
                await Task.Delay(QueueFullBackoff, _timeProvider, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Blocks for up to <see cref="FlushTimeout"/>: on shutdown there is nothing else to do meanwhile.</remarks>
    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        var undelivered = _producer.Flush(FlushTimeout);
        if (undelivered > 0)
        {
            LogFlushIncomplete(undelivered, FlushTimeout);
        }

        if (_droppedAfterFatal > 0)
        {
            LogDroppedAfterFatal(_droppedAfterFatal);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Releases the producer; messages not flushed by <see cref="CompleteAsync"/> are lost.</summary>
    public void Dispose() => _producer.Dispose();

    private bool TryProduce(Message<string, byte[]> message)
    {
        try
        {
            _producer.Produce(_topic, message, _onDelivered);
            return true;
        }
        catch (ProduceException<string, byte[]> ex) when (ex.Error.Code == ErrorCode.Local_QueueFull)
        {
            return false;
        }
    }

    private void OnDelivered(DeliveryReport<string, byte[]> report)
    {
        if (report.Error.IsError)
        {
            Interlocked.Increment(ref _failedDeliveries);
            // Logged once per episode: with the broker down every message fails.
            if (Interlocked.Increment(ref _failingDeliveries) == 1)
            {
                LogDeliveryFailing(report.Error.Reason);
            }

            return;
        }

        if (Volatile.Read(ref _failingDeliveries) > 0)
        {
            var failed = Interlocked.Exchange(ref _failingDeliveries, 0);
            if (failed > 0)
            {
                LogDeliveryResumed(failed);
            }
        }
    }

    private void OnError(IProducer<string, byte[]> producer, Error error)
    {
        if (!error.IsFatal)
        {
            // Transient (broker down, connection reset): the client reconnects by itself.
            LogBrokerError(error.Code, error.Reason);
        }
        else if (Interlocked.Exchange(ref _fatal, 1) == 0)
        {
            LogFatal(error.Code, error.Reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Positions are not being delivered: {Reason}")]
    private partial void LogDeliveryFailing(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Position delivery resumed; {Failed} messages were not delivered")]
    private partial void LogDeliveryResumed(long failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker error {Code}: {Reason}")]
    private partial void LogBrokerError(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Producer failed for good ({Code}: {Reason}); positions are dropped until the process restarts")]
    private partial void LogFatal(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Undelivered} positions were still undelivered after {Timeout}")]
    private partial void LogFlushIncomplete(int undelivered, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Dropped} positions were dropped after the producer failed")]
    private partial void LogDroppedAfterFatal(long dropped);
}

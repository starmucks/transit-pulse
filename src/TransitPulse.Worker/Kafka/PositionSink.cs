using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Npgsql;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Kafka;
using TransitPulse.Infrastructure.Positions;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Worker.Kafka;

/// <summary>
/// Reads <c>positions.enriched</c>, writes a hot Redis cadence then a warmer Timescale cadence (ADR-0015 / ADR-0018),
/// then commits offsets. Redis is flushed first so the map is not blocked on Postgres. Delivery stays at-least-once:
/// a crash repeats points; the primary key and GEO member overwrite collapse duplicates. A failed flush pauses further
/// consumes until the remaining buffer is written.
/// </summary>
internal sealed partial class PositionSink(
    IOptions<WorkerKafkaOptions> kafkaOptions,
    IOptions<PositionSinkOptions> sinkOptions,
    NpgsqlDataSource dataSource,
    IVehiclePositionStore positionStore,
    IVehicleGeoStore geoStore,
    TimeProvider timeProvider,
    PositionSinkMetrics metrics,
    ILogger<PositionSink> logger) : BackgroundService
{
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _flushRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly WorkerKafkaOptions _kafka = kafkaOptions.Value;
    private readonly PositionSinkOptions _sink = sinkOptions.Value;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stopping)
    {
        var applied = await SchemaMigrator.MigrateAsync(dataSource, stopping);
        LogMigrated(applied.Count == 0 ? "none" : string.Join(", ", applied));

        await KafkaTopicCreator.EnsureAsync(
            _kafka.BootstrapServers,
            [Topics.PositionsEnriched with { Name = _kafka.PositionsTopic }],
            stopping);

        var warmThinner = new PositionThinner(_sink.ThinningInterval);
        var hotThinner = new PositionThinner(_sink.HotThinningInterval);

        using var consumer = BuildConsumer();
        consumer.Subscribe(_kafka.PositionsTopic);

        try
        {
            var nextFlush = timeProvider.GetUtcNow() + _sink.FlushInterval;
            var nextCommit = timeProvider.GetUtcNow() + _kafka.CommitInterval;
            var pending = new List<TopicPartitionOffset>();
            var warmBuffer = new List<EnrichedPosition>();
            var hotBuffer = new List<EnrichedPosition>();
            var flushBlocked = false;
            while (!stopping.IsCancellationRequested)
            {
                // Do not grow the buffer while a prior flush is still failing (backpressure).
                if (!flushBlocked)
                {
                    var result = TryConsume(consumer);
                    if (result is { IsPartitionEOF: false })
                    {
                        RecordLag(result);
                        try
                        {
                            var enriched = EnrichedPositionMessage.Parse(result.Message.Value);
                            var feedId = enriched.Position.FeedId;
                            var vehicleId = enriched.Position.VehicleId;
                            var observedAt = enriched.Position.ObservedAt;

                            // Hot Redis first so the map sees fresh points without waiting for the warm window.
                            if (hotThinner.ShouldKeep(feedId, vehicleId, observedAt))
                            {
                                hotBuffer.Add(enriched);
                                hotThinner.Record(feedId, vehicleId, observedAt);
                            }

                            if (warmThinner.ShouldKeep(feedId, vehicleId, observedAt))
                            {
                                warmBuffer.Add(enriched);
                                warmThinner.Record(feedId, vehicleId, observedAt);
                            }
                            else
                            {
                                metrics.Thinned();
                            }
                        }
                        catch (Exception ex) when (ex is JsonException or ArgumentException)
                        {
                            LogPoison(result.TopicPartitionOffset.ToString(), ex.GetType().Name, ex.Message);
                        }

                        pending.Add(new TopicPartitionOffset(result.TopicPartition, result.Offset + 1));
                    }
                }

                var now = timeProvider.GetUtcNow();
                if (flushBlocked || now >= nextFlush)
                {
                    if (await TryFlushAsync(warmBuffer, hotBuffer, stopping))
                    {
                        flushBlocked = false;
                        nextFlush = now + _sink.FlushInterval;
                        if (now >= nextCommit)
                        {
                            Commit(consumer, pending);
                            nextCommit = now + _kafka.CommitInterval;
                        }
                    }
                    else
                    {
                        flushBlocked = true;
                        await Task.Delay(_flushRetryDelay, stopping);
                    }
                }
            }

            if (await TryFlushAsync(warmBuffer, hotBuffer, stopping))
            {
                Commit(consumer, pending);
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<bool> TryFlushAsync(
        List<EnrichedPosition> warmBuffer,
        List<EnrichedPosition> hotBuffer,
        CancellationToken stopping)
    {
        if (warmBuffer.Count == 0 && hotBuffer.Count == 0)
        {
            return true;
        }

        // Hot Redis first so the map is not blocked on Timescale per-row inserts (C3; amends ADR-0015 write order).
        if (hotBuffer.Count > 0)
        {
            try
            {
                await geoStore.UpsertManyAsync(hotBuffer, stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                metrics.RedisError();
                LogFlushFailed("redis", ex.GetType().Name, ex.Message);
                return false;
            }

            hotBuffer.Clear();
        }

        var warmCount = warmBuffer.Count;
        if (warmCount > 0)
        {
            try
            {
                await positionStore.InsertManyAsync(warmBuffer, stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                metrics.PostgresError();
                LogFlushFailed("postgres", ex.GetType().Name, ex.Message);
                return false;
            }

            metrics.Written(warmCount);
            warmBuffer.Clear();
        }

        return true;
    }

    private void RecordLag(ConsumeResult<string, byte[]> result)
    {
        if (result.Message.Timestamp.Type == TimestampType.NotAvailable)
        {
            return;
        }

        var stamped = result.Message.Timestamp.UtcDateTime;
        var lag = (timeProvider.GetUtcNow().UtcDateTime - stamped).TotalSeconds;
        metrics.Lag(lag);
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

    private void Commit(IConsumer<string, byte[]> consumer, List<TopicPartitionOffset> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var offsets = pending
            .GroupBy(offset => offset.TopicPartition)
            .Select(group => group.MaxBy(offset => offset.Offset.Value)!)
            .ToList();
        try
        {
            consumer.Commit(offsets);
            pending.Clear();
        }
        catch (KafkaException ex) when (!ex.Error.IsFatal)
        {
            LogCommitFailed(ex.Error.Code, ex.Error.Reason);
        }
    }

    private IConsumer<string, byte[]> BuildConsumer() => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
    {
        BootstrapServers = _kafka.BootstrapServers,
        ClientId = "transitpulse-worker-positions",
        GroupId = _kafka.PositionsGroupId,
        EnableAutoCommit = false,
        AutoOffsetReset = ParseAutoOffsetReset(_kafka.PositionsAutoOffsetReset),
        EnablePartitionEof = true,
    }).Build();

    private static AutoOffsetReset ParseAutoOffsetReset(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "LATEST" => AutoOffsetReset.Latest,
            "EARLIEST" => AutoOffsetReset.Earliest,
            _ => throw new InvalidOperationException(
                $"Kafka:PositionsAutoOffsetReset '{value}' is not Earliest or Latest."),
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema migrations applied: {Applied}")]
    private partial void LogMigrated(string applied);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read from the broker: {Code}: {Reason}")]
    private partial void LogConsumeError(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped unreadable enriched position at {Offset}: {ErrorType}: {Reason}")]
    private partial void LogPoison(string offset, string errorType, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Position sink {Sink} flush failed: {ErrorType}: {Reason}")]
    private partial void LogFlushFailed(string sink, string errorType, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commit failed, will retry: {Code}: {Reason}")]
    private partial void LogCommitFailed(ErrorCode code, string reason);
}

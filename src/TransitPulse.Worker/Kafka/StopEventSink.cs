using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Npgsql;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Kafka;
using TransitPulse.Infrastructure.StopEvents;

namespace TransitPulse.Worker.Kafka;

/// <summary>
/// Reads <c>events.stop</c> and upserts each event into Postgres by its deterministic id. Delivery stays at-least-once:
/// the Kafka offset moves only after the row is written, so a crash repeats the event and the upsert collapses it
/// (ADR-0014).
/// </summary>
internal sealed partial class StopEventSink(
    IOptions<WorkerKafkaOptions> options,
    NpgsqlDataSource dataSource,
    StopEventStore store,
    TimeProvider timeProvider,
    ILogger<StopEventSink> logger) : BackgroundService
{
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromMilliseconds(100);

    private readonly WorkerKafkaOptions _options = options.Value;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stopping)
    {
        // Safe if the importer also migrates: the advisory lock serialises the two callers.
        var applied = await SchemaMigrator.MigrateAsync(dataSource, stopping);
        LogMigrated(applied.Count == 0 ? "none" : string.Join(", ", applied));

        await KafkaTopicCreator.EnsureAsync(
            _options.BootstrapServers,
            [Topics.EventsStop with { Name = _options.StopEventsTopic }],
            stopping);

        using var consumer = BuildConsumer();
        consumer.Subscribe(_options.StopEventsTopic);

        try
        {
            var nextCommit = timeProvider.GetUtcNow() + _options.CommitInterval;
            var pending = new List<TopicPartitionOffset>();
            while (!stopping.IsCancellationRequested)
            {
                var result = TryConsume(consumer);
                if (result is { IsPartitionEOF: false })
                {
                    try
                    {
                        var stopEvent = StopEventMessage.Parse(result.Message.Value);
                        await store.UpsertAsync(stopEvent, stopping);
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException)
                    {
                        // Same contract as the Processor DLQ path: a poison record must not restart the process forever.
                        LogPoison(result.TopicPartitionOffset.ToString(), ex.GetType().Name, ex.Message);
                    }

                    pending.Add(new TopicPartitionOffset(result.TopicPartition, result.Offset + 1));
                }

                if (timeProvider.GetUtcNow() >= nextCommit)
                {
                    Commit(consumer, pending);
                    nextCommit = timeProvider.GetUtcNow() + _options.CommitInterval;
                }
            }

            Commit(consumer, pending);
        }
        finally
        {
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

    private void Commit(IConsumer<string, byte[]> consumer, List<TopicPartitionOffset> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        // One offset per partition: the highest next-to-read among the upserts since the last commit.
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
        BootstrapServers = _options.BootstrapServers,
        ClientId = "transitpulse-worker-stop-events",
        GroupId = _options.GroupId,
        EnableAutoCommit = false,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnablePartitionEof = true,
    }).Build();

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema migrations applied: {Applied}")]
    private partial void LogMigrated(string applied);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read from the broker: {Code}: {Reason}")]
    private partial void LogConsumeError(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped unreadable stop event at {Offset}: {ErrorType}: {Reason}")]
    private partial void LogPoison(string offset, string errorType, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commit failed, will retry: {Code}: {Reason}")]
    private partial void LogCommitFailed(ErrorCode code, string reason);
}

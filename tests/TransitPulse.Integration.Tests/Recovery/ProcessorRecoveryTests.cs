using System.Diagnostics.Metrics;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Kafka;
using TransitPulse.Infrastructure.StopEvents;
using TransitPulse.Integration.Tests.Kafka;
using TransitPulse.Integration.Tests.Postgres;
using TransitPulse.Integration.Tests.Redis;
using TransitPulse.Processor;
using TransitPulse.Processor.Kafka;
using TransitPulse.Worker.Kafka;

namespace TransitPulse.Integration.Tests.Recovery;

/// <summary>
/// Stage 2.10 / ADR-0014: crash windows around the Redis checkpoint still yield the same <c>stop_events</c> as an
/// uninterrupted run, and restart must load the checkpoint (not only Kafka committed offsets).
/// </summary>
public sealed class ProcessorRecoveryTests :
    IClassFixture<RedpandaFixture>,
    IClassFixture<PostgresFixture>,
    IClassFixture<RedisFixture>,
    IAsyncLifetime
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _day = new(2026, 11, 5);

    private readonly RedpandaFixture _redpanda;
    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private ConnectionMultiplexer? _redisConnection;

    public ProcessorRecoveryTests(RedpandaFixture redpanda, PostgresFixture postgres, RedisFixture redis)
    {
        _redpanda = redpanda;
        _postgres = postgres;
        _redis = redis;
    }

    public async ValueTask InitializeAsync() =>
        _redisConnection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        if (_redisConnection is not null)
        {
            await _redisConnection.DisposeAsync();
        }

        _services.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Crash_after_checkpoint_before_offset_commit_matches_uninterrupted_stop_events()
    {
        var baseline = await RunScenarioAsync(crash: CrashWindow.None);
        var recovered = await RunScenarioAsync(crash: CrashWindow.AfterCheckpoint);
        Assert.Equal(baseline, recovered);
    }

    [Fact]
    public async Task Crash_before_checkpoint_matches_uninterrupted_stop_events()
    {
        var baseline = await RunScenarioAsync(crash: CrashWindow.None);
        var recovered = await RunScenarioAsync(crash: CrashWindow.BeforeCheckpoint);
        Assert.Equal(baseline, recovered);
    }

    private async Task<IReadOnlyList<StopEventRow>> RunScenarioAsync(CrashWindow crash)
    {
        var database = await _postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var store = new StopEventStore(database);
        var topics = await CreateTopicsAsync(crash.ToString());
        var schedule = ScheduleWithStop();
        var drive = DriveThroughStop();

        var sink = new StopEventSink(
            Options.Create(new WorkerKafkaOptions
            {
                BootstrapServers = _redpanda.BootstrapServers,
                GroupId = $"{topics.GroupId}-sink",
                StopEventsTopic = topics.StopEvents,
                CommitInterval = TimeSpan.FromMilliseconds(200),
            }),
            database,
            store,
            TimeProvider.System,
            NullLogger<StopEventSink>.Instance);
        await sink.StartAsync(Ct);

        try
        {
            Publish(topics.Input, drive);

            var blobStore = new RedisCheckpointBlobStore(
                _redisConnection!,
                Options.Create(new ProcessorKafkaOptions
                {
                    GroupId = topics.GroupId,
                    InputTopic = topics.Input,
                }));
            var checkpoints = new CountingCheckpointStore(new ProcessorCheckpointStore(blobStore));
            var barrier = new FaultingCommitBarrier();
            Exception? saveFault = crash == CrashWindow.BeforeCheckpoint
                ? new InvalidOperationException("crash before checkpoint")
                : null;
            if (crash == CrashWindow.AfterCheckpoint)
            {
                barrier.Fault = new InvalidOperationException("crash after checkpoint");
            }

            var wrapping = new FaultingCheckpointStore(checkpoints, saveFault);
            var consumer = NewConsumer(topics, schedule, wrapping, barrier, commitInterval: TimeSpan.FromMilliseconds(200));
            await consumer.StartAsync(Ct);

            if (crash == CrashWindow.None)
            {
                var rows = await WaitForStopEventsAsync(database, 2);
                await consumer.StopAsync(Ct);
                Assert.False(consumer.ExecuteTask!.IsFaulted);
                return rows;
            }

            await WaitUntilAsync(() => consumer.ExecuteTask!.IsCompleted, TimeSpan.FromSeconds(30));
            Assert.True(consumer.ExecuteTask!.IsFaulted);
            var fault = consumer.ExecuteTask.Exception!.GetBaseException();
            Assert.Contains(
                crash == CrashWindow.AfterCheckpoint ? "crash after checkpoint" : "crash before checkpoint",
                fault.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, CommittedTotal(topics.Input, topics.GroupId));

            wrapping.SaveFault = null;
            barrier.Fault = null;
            if (crash == CrashWindow.AfterCheckpoint)
            {
                Assert.NotNull(blobStore.Load());
                var durable = wrapping.Load();
                Assert.NotNull(durable);
                Assert.Contains(durable!.Value.Partitions, part => part.State.NextOffset is > 0);
            }

            var loadsBeforeRestart = checkpoints.LoadCount;
            var restarted = NewConsumer(topics, schedule, wrapping, barrier, commitInterval: TimeSpan.FromMilliseconds(200));
            await restarted.StartAsync(Ct);
            await WaitUntilAsync(() => checkpoints.LoadCount > loadsBeforeRestart, TimeSpan.FromSeconds(20));
            Assert.True(checkpoints.LoadCount > loadsBeforeRestart, "Restart did not load the Redis checkpoint.");

            var recovered = await WaitForStopEventsAsync(database, 2);
            await restarted.StopAsync(Ct);
            Assert.False(restarted.ExecuteTask!.IsFaulted);
            return recovered;
        }
        finally
        {
            await sink.StopAsync(Ct);
            sink.Dispose();
        }
    }

    private PositionConsumer NewConsumer(
        RunTopics topics,
        CurrentTripSchedule schedule,
        IProcessorCheckpointStore checkpoints,
        ICommitBarrier barrier,
        TimeSpan commitInterval) =>
        new(
            Options.Create(new ProcessorKafkaOptions
            {
                BootstrapServers = _redpanda.BootstrapServers,
                GroupId = topics.GroupId,
                InputTopic = topics.Input,
                OutputTopic = topics.Output,
                StopEventsTopic = topics.StopEvents,
                AnomalyEventsTopic = topics.Anomaly,
                DeadLetterTopic = topics.DeadLetter,
                CommitInterval = commitInterval,
            }),
            schedule,
            checkpoints,
            new PositionMessageReader(),
            barrier,
            new ProcessorMetrics(_services.GetRequiredService<IMeterFactory>()),
            TimeProvider.System,
            NullLogger<PositionConsumer>.Instance);

    private async Task<RunTopics> CreateTopicsAsync(string name)
    {
        var suffix = $"{name}-{Guid.NewGuid():N}";
        var topics = new RunTopics(
            $"{suffix}-in",
            $"{suffix}-out",
            $"{suffix}-stop",
            $"{suffix}-anomaly",
            $"{suffix}-dlq",
            $"{suffix}-group");
        await KafkaTopicCreator.EnsureAsync(
            _redpanda.BootstrapServers,
            [
                new TopicDefinition(topics.Input, 3, TimeSpan.FromHours(1)),
                new TopicDefinition(topics.Output, 3, TimeSpan.FromHours(1)),
                new TopicDefinition(topics.StopEvents, 3, TimeSpan.FromHours(1)),
                new TopicDefinition(topics.Anomaly, 1, TimeSpan.FromHours(1)),
                new TopicDefinition(topics.DeadLetter, 1, TimeSpan.FromHours(1)),
            ],
            Ct);
        return topics;
    }

    private void Publish(string topic, params VehiclePosition[] positions)
    {
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            Partitioner = Partitioner.Murmur2Random,
        }).Build();
        foreach (var position in positions)
        {
            producer.Produce(topic, new Message<string, byte[]>
            {
                Key = PositionMessage.Key(position),
                Value = PositionMessage.Value(position),
            });
        }

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private long CommittedTotal(string input, string groupId)
    {
        using var client = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = groupId,
        }).Build();
        var partitions = Enumerable.Range(0, 3).Select(partition => new TopicPartition(input, partition));
        return client.Committed(partitions, TimeSpan.FromSeconds(10))
            .Where(offset => offset.Offset.Value >= 0)
            .Sum(offset => offset.Offset.Value);
    }

    private static CurrentTripSchedule ScheduleWithStop()
    {
        var line = new RouteShape([60.1, 60.3], [24.9, 24.9]);
        var stops = new StopPattern(line, [new StopPoint("1020453", 1, 60.2, 24.9)]);
        var schedule = new CurrentTripSchedule("hsl", TimeZoneInfo.Utc);
        schedule.Replace(new TripSchedule(
            [_day],
            [new ScheduledTrip("1009_14", "1009", 0, 14 * 3600, _day, line, stops)]));
        return schedule;
    }

    private static VehiclePosition[] DriveThroughStop()
    {
        const string trip = "1009/0/20261105/14:00:00";
        (int Seconds, double Lat)[] points =
            [(0, 60.1995), (5, 60.19985), (6, 60.19985), (7, 60.19985), (10, 60.2006), (11, 60.2007)];
        return
        [
            .. points.Select(point => new VehiclePosition(
                "hsl",
                "40/415",
                trip,
                "1009",
                point.Lat,
                24.9,
                8f,
                null,
                null,
                null,
                _t0.AddSeconds(point.Seconds))),
        ];
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Condition was not met in time.");
    }

    private static async Task<IReadOnlyList<StopEventRow>> WaitForStopEventsAsync(NpgsqlDataSource database, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await ReadStopEventsAsync(database);
            if (rows.Count >= count)
            {
                await Task.Delay(500, Ct);
                return await ReadStopEventsAsync(database);
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"Timed out waiting for {count} stop events.");
    }

    private static async Task<IReadOnlyList<StopEventRow>> ReadStopEventsAsync(NpgsqlDataSource database)
    {
        await using var command = database.CreateCommand(
            """
            SELECT id, feed_id, vehicle_id, trip_id, route_id, direction_id, service_date,
                   stop_id, stop_sequence, kind, at
            FROM public.stop_events
            ORDER BY id
            """);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<StopEventRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new StopEventRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt16(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return rows;
    }

    private enum CrashWindow
    {
        None,
        BeforeCheckpoint,
        AfterCheckpoint,
    }

    private sealed record RunTopics(
        string Input,
        string Output,
        string StopEvents,
        string Anomaly,
        string DeadLetter,
        string GroupId);

    private sealed record StopEventRow(
        string Id,
        string FeedId,
        string VehicleId,
        string TripId,
        string RouteId,
        short DirectionId,
        DateOnly ServiceDate,
        string StopId,
        int StopSequence,
        string Kind,
        DateTimeOffset At);

    private sealed class FaultingCommitBarrier : ICommitBarrier
    {
        public Exception? Fault { get; set; }

        public void AfterCheckpointSaved()
        {
            if (Fault is { } fault)
            {
                Fault = null;
                throw fault;
            }
        }
    }

    private sealed class CountingCheckpointStore(IProcessorCheckpointStore inner) : IProcessorCheckpointStore
    {
        public int LoadCount { get; private set; }

        public ProcessorCheckpointSnapshot? Load()
        {
            LoadCount++;
            return inner.Load();
        }

        public void Save(ProcessorCheckpointSnapshot snapshot) => inner.Save(snapshot);
    }

    private sealed class FaultingCheckpointStore(IProcessorCheckpointStore inner, Exception? saveFault) : IProcessorCheckpointStore
    {
        public Exception? SaveFault { get; set; } = saveFault;

        public ProcessorCheckpointSnapshot? LastLoaded { get; private set; }

        public ProcessorCheckpointSnapshot? Load()
        {
            LastLoaded = inner.Load();
            return LastLoaded;
        }

        public void Save(ProcessorCheckpointSnapshot snapshot)
        {
            if (SaveFault is { } fault)
            {
                SaveFault = null;
                throw fault;
            }

            inner.Save(snapshot);
        }
    }
}

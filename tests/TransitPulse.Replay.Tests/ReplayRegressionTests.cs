using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Infrastructure.Kafka;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Processor;
using TransitPulse.Processor.Kafka;
using TransitPulse.Replay.Tests.Kafka;
using TransitPulse.Replay.Tests.Postgres;

namespace TransitPulse.Replay.Tests;

/// <summary>
/// Stage 2.11: archive slice → normalize → Processor → stop events must match the committed golden file.
/// </summary>
public sealed class ReplayRegressionTests : IClassFixture<RedpandaFixture>, IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly string _fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hsl-tram-9-hour");
    private static readonly string _goldenPath = Path.Combine(_fixtureRoot, "expected-stop-events.jsonl");
    private static readonly DateTimeOffset _from = new(2026, 11, 4, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _to = _from.AddHours(1);
    private static readonly DateOnly[] _serviceDays =
    [
        new(2026, 11, 3),
        new(2026, 11, 4),
        new(2026, 11, 5),
    ];

    private readonly RedpandaFixture _redpanda;
    private readonly PostgresFixture _postgres;
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly string _work = Directory.CreateTempSubdirectory("replay-tests-").FullName;

    public ReplayRegressionTests(RedpandaFixture redpanda, PostgresFixture postgres)
    {
        _redpanda = redpanda;
        _postgres = postgres;
    }

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_work, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tram9_hour_stop_events_match_golden()
    {
        var database = await _postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var zip = ZipGtfs(Path.Combine(_work, "hsl-tram-9-hour.zip"));
        var imported = await new GtfsImporter(database).ImportAsync("hsl", zip, Ct);
        Assert.True(imported.Report!.IsValid, string.Join("; ", imported.Report.Problems));

        var loaded = await new TripScheduleReader(database).ReadAsync("hsl", _serviceDays, Ct);
        Assert.NotNull(loaded);
        var schedule = new CurrentTripSchedule("hsl", HelsinkiTimeZone());
        schedule.Replace(loaded!.Schedule);

        var topics = await CreateTopicsAsync();
        var positions = await NormalizeArchiveAsync();
        // ~151 k raw records for route 1009; ODO / incomplete drops leave the bulk still here.
        Assert.InRange(positions.Count, 100_000, 160_000);
        Publish(topics.Input, positions);

        var consumer = NewConsumer(topics, schedule);
        await consumer.StartAsync(Ct);
        try
        {
            await WaitUntilCaughtUpAsync(topics.Input, topics.GroupId, positions.Count);
        }
        finally
        {
            await consumer.StopAsync(Ct);
        }

        Assert.False(consumer.ExecuteTask!.IsFaulted, consumer.ExecuteTask.Exception?.ToString());
        var actual = ReadStopEvents(topics.StopEvents);
        Assert.NotEmpty(actual);

        if (UpdateGolden())
        {
            WriteGolden(actual);
            Assert.Fail($"Wrote {actual.Count} events to {_goldenPath}; re-run without REPLAY_UPDATE_GOLDEN.");
        }

        var expected = ReadGolden();
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected, actual);
    }

    private static async Task<List<VehiclePosition>> NormalizeArchiveAsync()
    {
        var normalizer = new HslPositionNormalizer();
        var drops = new CountingDrops();
        var positions = new List<VehiclePosition>();
        await foreach (var record in ArchiveReader.ReadAsync(Path.Combine(_fixtureRoot, "archive"), ["hsl"], _from, _to, Ct))
        {
            positions.AddRange(normalizer.Normalize(record, drops));
        }

        return positions;
    }

    private async Task<RunTopics> CreateTopicsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topics = new RunTopics(
            $"replay-{suffix}-in",
            $"replay-{suffix}-out",
            $"replay-{suffix}-stop",
            $"replay-{suffix}-anomaly",
            $"replay-{suffix}-dlq",
            $"replay-{suffix}-group");
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

    private PositionConsumer NewConsumer(RunTopics topics, CurrentTripSchedule schedule) =>
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
                CommitInterval = TimeSpan.FromMilliseconds(500),
            }),
            schedule,
            new MemoryProcessorCheckpointStore(),
            new PositionMessageReader(),
            new PassThroughCommitBarrier(),
            new ProcessorMetrics(_services.GetRequiredService<IMeterFactory>()),
            TimeProvider.System,
            NullLogger<PositionConsumer>.Instance);

    private void Publish(string topic, IReadOnlyList<VehiclePosition> positions)
    {
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            Partitioner = Partitioner.Murmur2Random,
            Acks = Acks.All,
            LingerMs = 5,
        }).Build();
        foreach (var position in positions)
        {
            producer.Produce(topic, new Message<string, byte[]>
            {
                Key = PositionMessage.Key(position),
                Value = PositionMessage.Value(position),
            });
        }

        producer.Flush(TimeSpan.FromMinutes(2));
    }

    private async Task WaitUntilCaughtUpAsync(string input, string groupId, int published)
    {
        // AdminClient reads offsets without joining the Processor's group. A second Consumer with the same
        // GroupId (the old Committed() poll) rebalances Redpanda and flaked CI with "Broker: Not coordinator".
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
        }).Build();
        var partitions = Enumerable.Range(0, 3)
            .Select(partition => new TopicPartition(input, new Partition(partition)))
            .ToList();

        var deadline = DateTime.UtcNow.AddMinutes(10);
        long last = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = await CommittedTotalAsync(admin, groupId, partitions);
                if (last >= published)
                {
                    return;
                }
            }
            catch (KafkaException)
            {
                // Transient coordinator blips while the Processor group is still settling
                // (includes ListConsumerGroupOffsetsException).
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException(
            $"Processor committed {last} of {published} positions within 10 minutes.");
    }

    private static async Task<long> CommittedTotalAsync(
        IAdminClient admin, string groupId, IReadOnlyList<TopicPartition> partitions)
    {
        var results = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(groupId, partitions.ToList())]);
        return results
            .SelectMany(result => result.Partitions)
            .Where(partition => !partition.Error.IsError && partition.Offset.Value >= 0)
            .Sum(partition => partition.Offset.Value);
    }

    private List<GoldenStopEvent> ReadStopEvents(string topic)
    {
        using var reader = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = $"golden-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = true,
        }).Build();
        reader.Subscribe(topic);

        var events = new List<GoldenStopEvent>();
        var eof = 0;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (eof < 3 && DateTime.UtcNow < deadline)
        {
            var result = reader.Consume(TimeSpan.FromSeconds(2));
            if (result is null)
            {
                continue;
            }

            if (result.IsPartitionEOF)
            {
                eof++;
                continue;
            }

            var stopEvent = StopEventMessage.Parse(result.Message.Value);
            events.Add(GoldenStopEvent.From(stopEvent));
        }

        reader.Close();
        return events.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static string ZipGtfs(string zipPath)
    {
        var gtfs = Path.Combine(_fixtureRoot, "gtfs");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(gtfs, zipPath);
        return zipPath;
    }

    private static TimeZoneInfo HelsinkiTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        }
    }

    private static bool UpdateGolden() =>
        string.Equals(Environment.GetEnvironmentVariable("REPLAY_UPDATE_GOLDEN"), "1", StringComparison.Ordinal);

    private static List<GoldenStopEvent> ReadGolden()
    {
        Assert.True(File.Exists(_goldenPath), $"Missing golden file {_goldenPath}. Run with REPLAY_UPDATE_GOLDEN=1 once.");
        return File.ReadAllLines(_goldenPath)
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize(line, GoldenJsonContext.Default.GoldenStopEvent)!)
            .ToList();
    }

    private static void WriteGolden(IReadOnlyList<GoldenStopEvent> events)
    {
        // Prefer the source fixture tree when running from the repo (not only under bin/).
        var sourceDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "hsl-tram-9-hour"));
        var path = Directory.Exists(sourceDir) ? Path.Combine(sourceDir, "expected-stop-events.jsonl") : _goldenPath;
        var builder = new StringBuilder();
        foreach (var stopEvent in events)
        {
            builder.AppendLine(JsonSerializer.Serialize(stopEvent, GoldenJsonContext.Default.GoldenStopEvent));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record RunTopics(
        string Input,
        string Output,
        string StopEvents,
        string Anomaly,
        string DeadLetter,
        string GroupId);

    private sealed class CountingDrops : IPositionDrops
    {
        public void Drop(string reason)
        {
        }
    }

    private sealed class MemoryProcessorCheckpointStore : IProcessorCheckpointStore
    {
        private ProcessorCheckpointSnapshot? _value;

        public ProcessorCheckpointSnapshot? Load() => _value;

        public void Save(ProcessorCheckpointSnapshot snapshot) => _value = snapshot;
    }
}

/// <summary>Stable golden row: id plus the fields that define the event.</summary>
public sealed record GoldenStopEvent(
    string Id,
    string FeedId,
    string VehicleId,
    string TripId,
    string RouteId,
    int DirectionId,
    string ServiceDate,
    string StopId,
    int StopSequence,
    string Kind,
    string At)
{
    public static GoldenStopEvent From(VehicleStopEvent stopEvent) => new(
        stopEvent.Id,
        stopEvent.FeedId,
        stopEvent.VehicleId,
        stopEvent.TripId,
        stopEvent.RouteId,
        stopEvent.DirectionId,
        stopEvent.ServiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        stopEvent.StopId,
        stopEvent.StopSequence,
        stopEvent.Kind.ToString(),
        stopEvent.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(GoldenStopEvent))]
internal sealed partial class GoldenJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

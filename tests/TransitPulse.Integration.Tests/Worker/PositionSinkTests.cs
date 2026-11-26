using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Kafka;
using TransitPulse.Infrastructure.Positions;
using TransitPulse.Infrastructure.Redis;
using TransitPulse.Integration.Tests.Kafka;
using TransitPulse.Integration.Tests.Postgres;
using TransitPulse.Integration.Tests.Redis;
using TransitPulse.Worker;
using TransitPulse.Worker.Kafka;

namespace TransitPulse.Integration.Tests.Worker;

public sealed class PositionSinkTests :
    IClassFixture<RedpandaFixture>,
    IClassFixture<PostgresFixture>,
    IClassFixture<RedisFixture>,
    IAsyncLifetime
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly RedpandaFixture _redpanda;
    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private ConnectionMultiplexer? _redisConnection;

    public PositionSinkTests(RedpandaFixture redpanda, PostgresFixture postgres, RedisFixture redis)
    {
        _redpanda = redpanda;
        _postgres = postgres;
        _redis = redis;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _redisConnection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);

    public ValueTask DisposeAsync()
    {
        _redisConnection?.Dispose();
        _services.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Sink_thins_fixture_stream_into_timescale_and_redis_geo()
    {
        var database = await _postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var topic = $"positions-sink-{Guid.NewGuid():N}";
        var groupId = $"{topic}-group";
        await KafkaTopicCreator.EnsureAsync(
            _redpanda.BootstrapServers,
            [new TopicDefinition(topic, 3, TimeSpan.FromHours(1))],
            Ct);

        var geo = new VehicleGeoStore(_redisConnection!, TimeSpan.FromSeconds(120));
        var sink = StartSink(database, topic, groupId, new VehiclePositionStore(database), geo);
        try
        {
            Publish(topic, DenseStream("40/415"));
            Publish(topic, DenseStream("40/416"));

            await WaitUntilAsync(async () => await CountPositionsAsync(database) >= 6, TimeSpan.FromSeconds(30));

            Assert.Equal(6, await CountPositionsAsync(database));
            Assert.Equal(3, await CountVehicleAsync(database, "40/415"));
            Assert.Equal(3, await CountVehicleAsync(database, "40/416"));

            var db = _redisConnection!.GetDatabase();
            Assert.NotNull(await db.GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), "40/415"));
            Assert.NotNull(await db.GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), "40/416"));
            Assert.True(await db.KeyExistsAsync(VehicleGeoStore.AttributeKey("hsl", "40/415")));
        }
        finally
        {
            await sink.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Failed_redis_flush_does_not_commit_offsets_until_retry_succeeds()
    {
        var database = await _postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var topic = $"positions-fail-{Guid.NewGuid():N}";
        var groupId = $"{topic}-group";
        await KafkaTopicCreator.EnsureAsync(
            _redpanda.BootstrapServers,
            [new TopicDefinition(topic, 3, TimeSpan.FromHours(1))],
            Ct);

        var realGeo = new VehicleGeoStore(_redisConnection!, TimeSpan.FromSeconds(120));
        // Gate failures instead of a fixed count: CommitInterval/retry are 200 ms, so a count of 3
        // often succeeds inside the old fixed delay and races the "still uncommitted" assert.
        var faultingGeo = new FaultingGeoStore(realGeo);
        var sink = StartSink(database, topic, groupId, new VehiclePositionStore(database), faultingGeo);
        try
        {
            // Unique vehicle id: RedisFixture is shared and GEO members outlive a failed hash write.
            const string vehicleId = "40/499-fail";
            var one = new EnrichedPosition(
                new VehiclePosition("hsl", vehicleId, "1009_trip", "1009", 60.17, 24.94, 8f, 180f, 0, null, _t0),
                null,
                0);
            Publish(topic, [one]);

            await WaitUntilAsync(async () => await CountPositionsAsync(database) >= 1, TimeSpan.FromSeconds(20));
            await WaitUntilAsync(() => Task.FromResult(faultingGeo.Attempts >= 2), TimeSpan.FromSeconds(10));
            Assert.Equal(0, await CommittedTotalAsync(groupId, topic));
            Assert.Null(await _redisConnection!.GetDatabase().GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), vehicleId));

            faultingGeo.AllowSuccess();
            await WaitUntilAsync(
                async () => await _redisConnection!.GetDatabase()
                    .GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), vehicleId) is not null,
                TimeSpan.FromSeconds(20));
            await WaitUntilAsync(async () => await CommittedTotalAsync(groupId, topic) >= 1, TimeSpan.FromSeconds(20));
            Assert.True(faultingGeo.Attempts >= 3);
        }
        finally
        {
            await sink.StopAsync(Ct);
        }
    }

    private PositionSink StartSink(
        Npgsql.NpgsqlDataSource database,
        string topic,
        string groupId,
        IVehiclePositionStore positions,
        IVehicleGeoStore geo)
    {
        var metrics = new PositionSinkMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var sink = new PositionSink(
            Options.Create(new WorkerKafkaOptions
            {
                BootstrapServers = _redpanda.BootstrapServers,
                GroupId = "transitpulse-worker-stop-events",
                PositionsGroupId = groupId,
                PositionsTopic = topic,
                CommitInterval = TimeSpan.FromMilliseconds(200),
            }),
            Options.Create(new PositionSinkOptions
            {
                ThinningInterval = TimeSpan.FromSeconds(10),
                HotThinningInterval = TimeSpan.FromMilliseconds(250),
                FlushInterval = TimeSpan.FromMilliseconds(200),
                GeoAttributeTtl = TimeSpan.FromSeconds(120),
            }),
            database,
            positions,
            geo,
            TimeProvider.System,
            metrics,
            NullLogger<PositionSink>.Instance);
        sink.StartAsync(Ct).GetAwaiter().GetResult();
        return sink;
    }

    /// <summary>Three keepers at 0/10/20 s plus fillers every second in between (22 messages).</summary>
    private static IEnumerable<EnrichedPosition> DenseStream(string vehicleId)
    {
        for (var second = 0; second <= 20; second++)
        {
            yield return new EnrichedPosition(
                new VehiclePosition(
                    "hsl",
                    vehicleId,
                    "1009_trip",
                    "1009",
                    60.17 + second * 0.0001,
                    24.94 + second * 0.0001,
                    8f,
                    180f,
                    second,
                    null,
                    _t0.AddSeconds(second)),
                new ScheduledProgress("1009_trip", "1009", 0, new DateOnly(2026, 11, 23), second * 10.0, null),
                second);
        }
    }

    private void Publish(string topic, IEnumerable<EnrichedPosition> positions)
    {
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            Partitioner = Partitioner.Murmur2Random,
        }).Build();
        foreach (var enriched in positions)
        {
            producer.Produce(topic, new Message<string, byte[]>
            {
                Key = EnrichedPositionMessage.Key(enriched),
                Value = EnrichedPositionMessage.Value(enriched),
            });
        }

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private async Task<long> CommittedTotalAsync(string groupId, string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
        }).Build();
        var partitions = Enumerable.Range(0, 3)
            .Select(partition => new TopicPartition(topic, new Partition(partition)))
            .ToList();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var results = await admin.ListConsumerGroupOffsetsAsync(
                    [new ConsumerGroupTopicPartitions(groupId, partitions)]);
                return results
                    .SelectMany(result => result.Partitions)
                    .Where(partition => !partition.Error.IsError && partition.Offset.Value >= 0)
                    .Sum(partition => partition.Offset.Value);
            }
            catch (KafkaException) when (attempt < 9)
            {
                await Task.Delay(200, Ct);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ready())
            {
                return;
            }

            await Task.Delay(200, Ct);
        }

        throw new TimeoutException($"Condition not met within {timeout}.");
    }

    private static async Task<int> CountPositionsAsync(Npgsql.NpgsqlDataSource database)
    {
        await using var command = database.CreateCommand("SELECT count(*)::int FROM public.vehicle_positions");
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<int> CountVehicleAsync(Npgsql.NpgsqlDataSource database, string vehicleId)
    {
        await using var command = database.CreateCommand(
            "SELECT count(*)::int FROM public.vehicle_positions WHERE vehicle_id = $1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = vehicleId });
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class FaultingGeoStore(IVehicleGeoStore inner) : IVehicleGeoStore
    {
        private volatile bool _fail = true;

        public int Attempts { get; private set; }

        public void AllowSuccess() => _fail = false;

        public async Task UpsertManyAsync(
            IReadOnlyList<EnrichedPosition> positions,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (_fail)
            {
                throw new InvalidOperationException($"forced redis failure #{Attempts}");
            }

            await inner.UpsertManyAsync(positions, cancellationToken);
        }
    }
}

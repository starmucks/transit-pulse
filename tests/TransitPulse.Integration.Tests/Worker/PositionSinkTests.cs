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
        await KafkaTopicCreator.EnsureAsync(
            _redpanda.BootstrapServers,
            [new TopicDefinition(topic, 3, TimeSpan.FromHours(1))],
            Ct);

        var store = new VehiclePositionStore(database);
        var metrics = new PositionSinkMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var sink = new PositionSink(
            Options.Create(new WorkerKafkaOptions
            {
                BootstrapServers = _redpanda.BootstrapServers,
                PositionsGroupId = $"{topic}-group",
                PositionsTopic = topic,
                CommitInterval = TimeSpan.FromMilliseconds(200),
            }),
            Options.Create(new PositionSinkOptions
            {
                ThinningInterval = TimeSpan.FromSeconds(10),
                GeoAttributeTtl = TimeSpan.FromSeconds(120),
            }),
            database,
            _redisConnection!,
            store,
            TimeProvider.System,
            metrics,
            NullLogger<PositionSink>.Instance);

        await sink.StartAsync(Ct);
        try
        {
            Publish(topic, DenseStream("40/415"));
            Publish(topic, DenseStream("40/416"));

            await WaitUntilAsync(async () => await CountPositionsAsync(database) >= 6, TimeSpan.FromSeconds(30));

            Assert.Equal(6, await CountPositionsAsync(database));
            Assert.Equal(3, await CountVehicleAsync(database, "40/415"));
            Assert.Equal(3, await CountVehicleAsync(database, "40/416"));

            var geo = _redisConnection!.GetDatabase();
            Assert.NotNull(await geo.GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), "40/415"));
            Assert.NotNull(await geo.GeoPositionAsync(VehicleGeoStore.GeoKey("hsl"), "40/416"));
            Assert.True(await geo.KeyExistsAsync(VehicleGeoStore.AttributeKey("hsl", "40/415")));
        }
        finally
        {
            await sink.StopAsync(Ct);
        }
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
}

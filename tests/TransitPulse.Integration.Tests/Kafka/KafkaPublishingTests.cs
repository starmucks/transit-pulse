using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Kafka;

namespace TransitPulse.Integration.Tests.Kafka;

public sealed class KafkaPublishingTests(RedpandaFixture redpanda) : IClassFixture<RedpandaFixture>
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VehiclePosition At(string vehicleId, int seconds) =>
        new("hsl", vehicleId, null, "1009", 60.2 + seconds / 1000.0, 24.9, 8f, null, -30, OccupancyLevel.ManySeatsAvailable, _t0.AddSeconds(seconds));

    private async Task CreateTopicAsync(string topic) =>
        await new KafkaTopicInitializer(
            Options.Create(new KafkaOptions { BootstrapServers = redpanda.BootstrapServers, PositionsTopic = topic }),
            NullLogger<KafkaTopicInitializer>.Instance).StartAsync(Ct);

    [Fact]
    public async Task Topic_is_created_with_partitions_and_retention_and_kept_on_restart()
    {
        await CreateTopicAsync("positions-topic-test");
        // A second start finds the topic and leaves it as it is.
        await CreateTopicAsync("positions-topic-test");

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = redpanda.BootstrapServers }).Build();
        var metadata = admin.GetMetadata("positions-topic-test", TimeSpan.FromSeconds(10));
        var configs = await admin.DescribeConfigsAsync([new ConfigResource { Type = ResourceType.Topic, Name = "positions-topic-test" }]);

        Assert.Equal(12, Assert.Single(metadata.Topics).Partitions.Count);
        Assert.Equal("86400000", Assert.Single(configs).Entries["retention.ms"].Value);
    }

    [Fact]
    public async Task Published_positions_are_read_back_with_each_vehicle_in_one_partition_in_order()
    {
        const string topic = "positions-publish-test";
        await CreateTopicAsync(topic);
        VehiclePosition[] published = [.. Enumerable.Range(0, 5).Select(s => At("40/415", s)), .. Enumerable.Range(0, 3).Select(s => At("12/7", s))];

        using (var sink = new KafkaSink(new ProducerConfig { BootstrapServers = redpanda.BootstrapServers }, topic, TimeProvider.System, NullLogger<KafkaSink>.Instance))
        {
            foreach (var position in published)
            {
                await sink.PublishAsync([position], Ct);
            }

            await sink.CompleteAsync(Ct);
            Assert.Equal(0, sink.FailedDeliveries);
        }

        var received = Consume(topic, published.Length);

        Assert.Equal(published.Length, received.Count);
        foreach (var vehicle in received.GroupBy(message => message.Message.Key))
        {
            Assert.Single(vehicle.Select(message => message.Partition.Value).Distinct());
            var times = vehicle.Select(message => PositionMessage.Parse(message.Message.Value).ObservedAt).ToList();
            Assert.Equal(times.Order(), times);
        }

        var first = received.First(message => message.Message.Key == "hsl/40/415");
        Assert.Equal(At("40/415", 0), PositionMessage.Parse(first.Message.Value));
    }

    private List<ConsumeResult<string, byte[]>> Consume(string topic, int count)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);

        var received = new List<ConsumeResult<string, byte[]>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (received.Count < count && DateTime.UtcNow < deadline)
        {
            if (consumer.Consume(TimeSpan.FromSeconds(1)) is { } result)
            {
                received.Add(result);
            }
        }

        consumer.Close();
        return received;
    }
}

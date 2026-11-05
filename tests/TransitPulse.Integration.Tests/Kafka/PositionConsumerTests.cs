using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Kafka;
using TransitPulse.Processor;
using TransitPulse.Processor.Kafka;

namespace TransitPulse.Integration.Tests.Kafka;

public sealed class PositionConsumerTests(RedpandaFixture redpanda) : IClassFixture<RedpandaFixture>, IDisposable
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VehiclePosition At(string vehicleId, int seconds, double lat = 60.2) =>
        new("hsl", vehicleId, null, "1009", lat, 24.9, 8f, null, null, null, _t0.AddSeconds(seconds));

    private async Task<(string Input, string Output)> CreateTopicsAsync(string name)
    {
        (string input, string output) = ($"{name}-in", $"{name}-out");
        await KafkaTopicCreator.EnsureAsync(
            redpanda.BootstrapServers,
            [new TopicDefinition(input, 3, TimeSpan.FromHours(1)), new TopicDefinition(output, 3, TimeSpan.FromHours(1))],
            Ct);
        return (input, output);
    }

    private PositionConsumer NewConsumer(string input, string output, TimeSpan? commitInterval = null, ILogger<PositionConsumer>? logger = null) => new(
        Options.Create(new ProcessorKafkaOptions
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"{input}-group",
            InputTopic = input,
            OutputTopic = output,
            // Long by default: a test sees the commit it provokes, not one the timer happened to make.
            CommitInterval = commitInterval ?? TimeSpan.FromHours(1),
        }),
        new ProcessorMetrics(_services.GetRequiredService<IMeterFactory>()),
        TimeProvider.System,
        logger ?? NullLogger<PositionConsumer>.Instance);

    private void Publish(string topic, params VehiclePosition[] positions)
    {
        using var producer = NewProducer();
        foreach (var position in positions)
        {
            producer.Produce(topic, new Message<string, byte[]> { Key = PositionMessage.Key(position), Value = PositionMessage.Value(position) });
        }

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private IProducer<string, byte[]> NewProducer() =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = redpanda.BootstrapServers, Partitioner = Partitioner.Murmur2Random }).Build();

    /// <summary>Reads until <paramref name="count"/> messages arrived, then a little longer to catch any extra one.</summary>
    private List<ConsumeResult<string, byte[]>> ReadOutput(string topic, int count)
    {
        using var reader = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        reader.Subscribe(topic);

        var received = new List<ConsumeResult<string, byte[]>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (received.Count < count && DateTime.UtcNow < deadline)
        {
            if (reader.Consume(TimeSpan.FromSeconds(1)) is { } result)
            {
                received.Add(result);
            }
        }

        var quietUntil = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < quietUntil)
        {
            if (reader.Consume(TimeSpan.FromMilliseconds(200)) is { } result)
            {
                received.Add(result);
            }
        }

        reader.Close();
        return received;
    }

    /// <summary>The offsets the group has committed for the topic's partitions, summed: how many messages it finished.</summary>
    private long CommittedTotal(string input)
    {
        using var client = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"{input}-group",
        }).Build();
        var partitions = Enumerable.Range(0, 3).Select(partition => new TopicPartition(input, partition));
        return client.Committed(partitions, TimeSpan.FromSeconds(10)).Where(offset => offset.Offset.Value >= 0).Sum(offset => offset.Offset.Value);
    }

    [Fact]
    public async Task Plausible_positions_are_published_in_order_and_the_rest_are_dropped()
    {
        var (input, output) = await CreateTopicsAsync("flow");
        var keptAfterJump = At("40/415", 3);
        Publish(input, At("40/415", 0), At("40/415", 1), At("40/415", 2, lat: 61.5), keptAfterJump, At("12/7", 0));
        using (var producer = NewProducer())
        {
            producer.Produce(input, new Message<string, byte[]> { Key = "hsl/40/415", Value = Encoding.UTF8.GetBytes("not json") });
            producer.Flush(TimeSpan.FromSeconds(10));
        }

        var consumer = NewConsumer(input, output);
        await consumer.StartAsync(Ct);
        var received = ReadOutput(output, 4);
        await consumer.StopAsync(Ct);

        Assert.False(consumer.ExecuteTask!.IsFaulted);
        var vehicle = received.Where(message => message.Message.Key == "hsl/40/415").Select(message => PositionMessage.Parse(message.Message.Value)).ToList();
        Assert.Equal([At("40/415", 0), At("40/415", 1), keptAfterJump], vehicle);
        Assert.Equal(4, received.Count);
    }

    [Fact]
    public async Task Metrics_count_every_verdict_and_lag_falls_to_zero()
    {
        var factory = _services.GetRequiredService<IMeterFactory>();
        using var messages = new MetricCollector<long>(factory, ProcessorMetrics.MeterName, "transitpulse.processor.messages");
        using var lagTime = new MetricCollector<double>(factory, ProcessorMetrics.MeterName, "transitpulse.processor.lag_time");
        using var lag = new MetricCollector<long>(factory, ProcessorMetrics.MeterName, "transitpulse.processor.lag");
        var (input, output) = await CreateTopicsAsync("metrics");
        Publish(input, At("40/415", 5), At("40/415", 1), At("40/415", 6, lat: 61.5), At("40/415", 7));

        var consumer = NewConsumer(input, output);
        await consumer.StartAsync(Ct);
        ReadOutput(output, 2);

        // The lag is updated once a second, and the client learns the high watermark from its fetches.
        lag.RecordObservableInstruments();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (lag.LastMeasurement is not { Value: 0 } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, Ct);
            lag.RecordObservableInstruments();
        }

        await consumer.StopAsync(Ct);

        int Count(string verdict) => messages.GetMeasurementSnapshot().Count(m => (string?)m.Tags["verdict"] == verdict);
        Assert.Equal(2, Count("accepted"));
        Assert.Equal(1, Count("out_of_order"));
        Assert.Equal(1, Count("impossible_jump"));
        Assert.Equal(4, lagTime.GetMeasurementSnapshot().Count);
        Assert.Equal(0, lag.LastMeasurement?.Value);
    }

    [Fact]
    public async Task A_vehicle_keeps_its_partition_number_from_input_to_output()
    {
        var (input, output) = await CreateTopicsAsync("partition");
        VehiclePosition[] positions = [.. Enumerable.Range(0, 12).Select(vehicle => At($"40/{vehicle}", 0))];
        Publish(input, positions);

        var consumer = NewConsumer(input, output);
        await consumer.StartAsync(Ct);
        var outputMessages = ReadOutput(output, positions.Length);
        await consumer.StopAsync(Ct);
        var inputMessages = ReadOutput(input, positions.Length);

        Assert.Equal(positions.Length, outputMessages.Count);
        Assert.Equal(
            inputMessages.Select(message => (message.Message.Key, message.Partition.Value)).Order(),
            outputMessages.Select(message => (message.Message.Key, message.Partition.Value)).Order());
    }

    [Fact]
    public async Task After_a_clean_stop_the_next_start_continues_without_repeating()
    {
        var (input, output) = await CreateTopicsAsync("restart");
        Publish(input, [.. Enumerable.Range(0, 5).Select(s => At("40/415", s))]);

        var first = NewConsumer(input, output);
        await first.StartAsync(Ct);
        Assert.Equal(5, ReadOutput(output, 5).Count);
        await first.StopAsync(Ct);
        Assert.Equal(5, CommittedTotal(input));

        Publish(input, [.. Enumerable.Range(5, 3).Select(s => At("40/415", s))]);
        var second = NewConsumer(input, output);
        await second.StartAsync(Ct);
        var received = ReadOutput(output, 8);
        await second.StopAsync(Ct);

        Assert.Equal(8, received.Count);
        Assert.Equal(8, CommittedTotal(input));
    }

    [Fact]
    public async Task Partitions_move_between_instances_without_losses_or_repeats()
    {
        var (input, output) = await CreateTopicsAsync("rebalance");
        var firstLog = new FakeLogger<PositionConsumer>();
        var published = 0;

        // Every vehicle's positions are one apart in time, so each batch is new for all of them.
        void PublishRound(int round) => Publish(input, [.. Enumerable.Range(0, 12).Select(vehicle => At($"40/{vehicle}", round))]);

        var first = NewConsumer(input, output, logger: firstLog);
        await first.StartAsync(Ct);
        PublishRound(0);
        published += 12;
        Assert.Equal(published, ReadOutput(output, published).Count);

        var second = NewConsumer(input, output);
        await second.StartAsync(Ct);
        await WaitForMembersAsync($"{input}-group", 2);

        PublishRound(1);
        published += 12;
        Assert.Equal(published, ReadOutput(output, published).Count);

        // Cooperative: the first instance gave up only what moved, not all three partitions.
        var revoked = Assert.Single(firstLog.Collector.GetSnapshot(), record => record.Message.StartsWith("Partitions revoked", StringComparison.Ordinal));
        Assert.InRange(revoked.Message.Split(',').Length, 1, 2);

        await second.StopAsync(Ct);
        await WaitForMembersAsync($"{input}-group", 1);

        PublishRound(2);
        published += 12;
        var received = ReadOutput(output, published);
        await first.StopAsync(Ct);

        Assert.False(first.ExecuteTask!.IsFaulted);
        Assert.False(second.ExecuteTask!.IsFaulted);
        // No loss and no repeat: the commit interval is an hour, so only the commits at the handovers avoid repeats.
        Assert.Equal(published, received.Count);
        Assert.Equal(published, received.Select(message => (message.Message.Key, PositionMessage.Parse(message.Message.Value).ObservedAt)).Distinct().Count());
    }

    /// <summary>Waits until the group has that many members and each of them holds at least one partition.</summary>
    private async Task WaitForMembersAsync(string group, int members)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = redpanda.BootstrapServers }).Build();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var description = await admin.DescribeConsumerGroupsAsync([group]);
            var current = description.ConsumerGroupDescriptions[0].Members;
            if (current.Count == members && current.All(member => member.Assignment.TopicPartitions.Count > 0))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        Assert.Fail($"The group {group} did not reach {members} members with partitions in time.");
    }

    [Fact]
    public async Task Progress_is_committed_on_the_interval_while_running()
    {
        var (input, output) = await CreateTopicsAsync("interval");
        Publish(input, [.. Enumerable.Range(0, 5).Select(s => At("40/415", s))]);

        var consumer = NewConsumer(input, output, commitInterval: TimeSpan.FromMilliseconds(200));
        await consumer.StartAsync(Ct);
        ReadOutput(output, 5);

        // Read while the loop is still running: only the timer can have committed by now.
        var committed = CommittedTotal(input);
        await consumer.StopAsync(Ct);

        Assert.Equal(5, committed);
    }
}

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Kafka;

namespace TransitPulse.Integration.Tests.Kafka;

public sealed class KafkaTopicCreatorTests(RedpandaFixture redpanda) : IClassFixture<RedpandaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TopicDefinition Topic(string name) => new(name, 2, TimeSpan.FromHours(1));

    private Task<IReadOnlyList<string>> EnsureAsync(params TopicDefinition[] topics) =>
        KafkaTopicCreator.EnsureAsync(redpanda.BootstrapServers, topics, Ct);

    [Fact]
    public async Task Topics_are_created_with_their_partitions_and_retention()
    {
        var definition = new TopicDefinition("creator-settings", 3, TimeSpan.FromDays(7));

        var created = await EnsureAsync(definition);

        Assert.Equal(["creator-settings"], created);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = redpanda.BootstrapServers }).Build();
        var metadata = admin.GetMetadata(definition.Name, TimeSpan.FromSeconds(10));
        var configs = await admin.DescribeConfigsAsync([new ConfigResource { Type = ResourceType.Topic, Name = definition.Name }]);
        Assert.Equal(3, Assert.Single(metadata.Topics).Partitions.Count);
        Assert.Equal("604800000", Assert.Single(configs).Entries["retention.ms"].Value);
    }

    [Fact]
    public async Task Second_call_creates_nothing()
    {
        await EnsureAsync(Topic("creator-repeat-1"), Topic("creator-repeat-2"));

        var created = await EnsureAsync(Topic("creator-repeat-1"), Topic("creator-repeat-2"));

        Assert.Empty(created);
    }

    [Fact]
    public async Task Missing_topics_are_created_when_others_already_exist()
    {
        await EnsureAsync(Topic("creator-mixed-existing"));

        var created = await EnsureAsync(Topic("creator-mixed-existing"), Topic("creator-mixed-new"));

        Assert.Equal(["creator-mixed-new"], created);
    }

    [Fact]
    public async Task A_topic_the_broker_refuses_is_an_error()
    {
        var failure = await Assert.ThrowsAsync<CreateTopicsException>(() => EnsureAsync(Topic("creator-valid"), Topic("not a valid name")));

        Assert.Contains(failure.Results, result => result.Topic == "not a valid name" && result.Error.IsError);
    }
}

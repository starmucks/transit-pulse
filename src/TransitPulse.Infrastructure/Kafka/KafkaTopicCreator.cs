using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using TransitPulse.Domain;

namespace TransitPulse.Infrastructure.Kafka;

/// <summary>
/// Creates topics in code rather than leaving them to the broker: a topic auto-created on first write gets one
/// partition, which silently caps consumers at one.
/// </summary>
public static class KafkaTopicCreator
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates the topics that do not exist yet; an existing topic is left as it is, whatever its settings.</summary>
    /// <param name="bootstrapServers">Comma-separated <c>host:port</c> list of brokers.</param>
    /// <param name="topics">The topics to declare.</param>
    /// <param name="cancellationToken">Stops waiting for the broker.</param>
    /// <returns>Names of the topics this call created.</returns>
    /// <exception cref="CreateTopicsException">The broker refused a topic for a reason other than it already existing.</exception>
    /// <exception cref="KafkaException">The broker did not answer within 30 s.</exception>
    public static async Task<IReadOnlyList<string>> EnsureAsync(
        string bootstrapServers,
        IEnumerable<TopicDefinition> topics,
        CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        var specifications = topics.Select(ToSpecification).ToList();

        try
        {
            await admin.CreateTopicsAsync(specifications, new CreateTopicsOptions { RequestTimeout = _requestTimeout })
                .WaitAsync(cancellationToken);
            return [.. specifications.Select(specification => specification.Name)];
        }
        // The exception lists every topic of the request, including those created by it: "all already existed" would
        // turn "one existed, the others were created" into a failure.
        catch (CreateTopicsException ex) when (ex.Results.All(result => result.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            return [.. ex.Results.Where(result => result.Error.Code == ErrorCode.NoError).Select(result => result.Topic)];
        }
    }

    private static TopicSpecification ToSpecification(TopicDefinition topic) => new()
    {
        Name = topic.Name,
        NumPartitions = topic.Partitions,
        // -1: the broker's default, so one setting serves a single local node and a replicated cluster.
        ReplicationFactor = -1,
        Configs = new() { ["retention.ms"] = ((long)topic.Retention.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) },
    };
}

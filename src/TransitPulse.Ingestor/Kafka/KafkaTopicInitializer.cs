using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace TransitPulse.Ingestor.Kafka;

/// <summary>
/// Creates the positions topic at startup, before the pipeline publishes anything. A broker that creates topics on
/// first write would make it with one partition, which silently caps consumers at one; an existing topic is kept as is.
/// </summary>
/// <param name="options">Broker and topic settings.</param>
/// <param name="logger">Startup log.</param>
internal sealed partial class KafkaTopicInitializer(IOptions<KafkaOptions> options, ILogger<KafkaTopicInitializer> logger) : IHostedService
{
    /// <summary>How long to wait for the broker; a broker still down by then fails the startup.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = settings.BootstrapServers }).Build();
        var topic = new TopicSpecification
        {
            Name = settings.PositionsTopic,
            NumPartitions = settings.PositionsPartitions,
            // -1: the broker's default, so one setting serves a single local node and a replicated cluster.
            ReplicationFactor = -1,
            Configs = new() { ["retention.ms"] = ((long)settings.PositionsRetention.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) },
        };

        try
        {
            await admin.CreateTopicsAsync([topic], new CreateTopicsOptions { RequestTimeout = RequestTimeout }).WaitAsync(cancellationToken);
            LogCreated(topic.Name, topic.NumPartitions);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            LogExists(topic.Name);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Created topic {Topic} with {Partitions} partitions")]
    private partial void LogCreated(string topic, int partitions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Topic {Topic} already exists; its settings are left as they are")]
    private partial void LogExists(string topic);
}

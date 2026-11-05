using Microsoft.Extensions.Options;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Kafka;

namespace TransitPulse.Processor.Kafka;

/// <summary>
/// Creates the topics the Processor reads and writes before anything uses them. The input is included because the
/// Processor may start before the Ingestor, and a consumer subscribed to a missing topic notices it only at its next
/// metadata refresh.
/// </summary>
/// <param name="options">Broker settings.</param>
/// <param name="logger">Startup log.</param>
internal sealed partial class ProcessorTopicInitializer(IOptions<ProcessorKafkaOptions> options, ILogger<ProcessorTopicInitializer> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        // Input and output by their configured names, so a run on other topics gets the same partitions: with fewer, a
        // vehicle would not keep its partition number from input to output.
        TopicDefinition[] topics =
        [
            Topics.PositionsRaw with { Name = settings.InputTopic },
            Topics.PositionsEnriched with { Name = settings.OutputTopic },
            Topics.EventsStop,
            Topics.EventsAnomaly,
            Topics.PositionsRawDlq,
        ];
        var created = await KafkaTopicCreator.EnsureAsync(settings.BootstrapServers, topics, cancellationToken);
        LogTopics(topics.Length, created.Count == 0 ? "none" : string.Join(", ", created));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Declared {Declared} Kafka topics; created: {Created}")]
    private partial void LogTopics(int declared, string created);
}

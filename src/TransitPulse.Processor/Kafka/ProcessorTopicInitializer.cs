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
    private static readonly TopicDefinition[] _topics =
        [Topics.PositionsRaw, Topics.PositionsEnriched, Topics.EventsStop, Topics.EventsAnomaly, Topics.PositionsRawDlq];

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var created = await KafkaTopicCreator.EnsureAsync(options.Value.BootstrapServers, _topics, cancellationToken);
        LogTopics(_topics.Length, created.Count == 0 ? "none" : string.Join(", ", created));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Declared {Declared} Kafka topics; created: {Created}")]
    private partial void LogTopics(int declared, string created);
}

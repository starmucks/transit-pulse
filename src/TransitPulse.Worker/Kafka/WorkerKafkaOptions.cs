using TransitPulse.Domain;

namespace TransitPulse.Worker.Kafka;

/// <summary>Broker and consumer settings for the Worker, configuration section <c>Kafka</c>.</summary>
internal sealed class WorkerKafkaOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kafka";

    /// <summary>Comma-separated <c>host:port</c> list of brokers to connect to first.</summary>
    public string BootstrapServers { get; set; } = "";

    /// <summary>Consumer group for the stop-event sink.</summary>
    public string GroupId { get; set; } = "transitpulse-worker-stop-events";

    /// <summary>Topic to read stop events from.</summary>
    public string StopEventsTopic { get; set; } = Topics.EventsStop.Name;

    /// <summary>
    /// How often offsets are committed after successful upserts. Each commit waits for the last write, so a shorter
    /// interval costs throughput.
    /// </summary>
    public TimeSpan CommitInterval { get; set; } = TimeSpan.FromSeconds(5);
}

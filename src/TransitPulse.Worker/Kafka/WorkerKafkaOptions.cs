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

    /// <summary>Consumer group for the enriched-position sink (must differ from <see cref="GroupId"/>).</summary>
    public string PositionsGroupId { get; set; } = "transitpulse-worker-positions";

    /// <summary>Topic to read enriched positions from.</summary>
    public string PositionsTopic { get; set; } = Topics.PositionsEnriched.Name;

    /// <summary>
    /// How often offsets are committed after successful upserts. Each commit waits for the last write, so a shorter
    /// interval costs throughput. Shared by both sinks.
    /// </summary>
    public TimeSpan CommitInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Offset reset for the position sink only (<c>Earliest</c> or <c>Latest</c>). Stop-event sink stays Earliest.
    /// </summary>
    public string PositionsAutoOffsetReset { get; set; } = "Earliest";
}

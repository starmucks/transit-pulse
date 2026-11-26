using TransitPulse.Domain;

namespace TransitPulse.Processor.Kafka;

/// <summary>Broker and consumer settings, configuration section <c>Kafka</c>.</summary>
internal sealed class ProcessorKafkaOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kafka";

    /// <summary>Comma-separated <c>host:port</c> list of brokers to connect to first.</summary>
    public string BootstrapServers { get; set; } = "";

    /// <summary>Consumer group: Processor instances with the same id share the partitions and continue each other's work.</summary>
    public string GroupId { get; set; } = "transitpulse-processor";

    /// <summary>Topic to read positions from.</summary>
    public string InputTopic { get; set; } = Topics.PositionsRaw.Name;

    /// <summary>Topic to publish accepted positions to.</summary>
    public string OutputTopic { get; set; } = Topics.PositionsEnriched.Name;

    /// <summary>Topic to publish stop events to; another name keeps a measurement run out of the real one.</summary>
    public string StopEventsTopic { get; set; } = Topics.EventsStop.Name;

    /// <summary>Topic to publish anomaly events to; another name keeps a measurement run out of the real one.</summary>
    public string AnomalyEventsTopic { get; set; } = Topics.EventsAnomaly.Name;

    /// <summary>Topic for source records that remain unreadable after <see cref="ProcessingAttempts"/>.</summary>
    public string DeadLetterTopic { get; set; } = Topics.PositionsRawDlq.Name;

    /// <summary>Maximum decode and validation attempts before publishing one source record to the dead-letter topic.</summary>
    public int ProcessingAttempts { get; set; } = 3;

    /// <summary>
    /// How often progress is saved: about what is processed again after a crash, or more if the last commits failed. Each
    /// commit waits for every published message to be delivered, so a shorter interval costs throughput.
    /// </summary>
    public TimeSpan CommitInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Where to start when a partition has no committed offset. Default <c>Earliest</c> keeps history;
    /// <c>Latest</c> is for a local cold start that must not replay a retained backlog against a newer GTFS schedule.
    /// </summary>
    public string AutoOffsetReset { get; set; } = "Earliest";
}

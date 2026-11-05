namespace TransitPulse.Processor.Kafka;

/// <summary>Broker settings, configuration section <c>Kafka</c>.</summary>
internal sealed class ProcessorKafkaOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kafka";

    /// <summary>Comma-separated <c>host:port</c> list of brokers to connect to first.</summary>
    public string BootstrapServers { get; set; } = "";
}

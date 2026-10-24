namespace TransitPulse.Ingestor.Kafka;

/// <summary>Broker and topic settings, configuration section <c>Kafka</c>; used only in <c>full</c> mode.</summary>
internal sealed class KafkaOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kafka";

    /// <summary>Comma-separated <c>host:port</c> list of brokers to connect to first.</summary>
    public string BootstrapServers { get; set; } = "";

    /// <summary>Topic for normalized positions.</summary>
    public string PositionsTopic { get; set; } = "positions.raw";

    /// <summary>Partitions of the positions topic when it is created; the ceiling for parallel consumers.</summary>
    public int PositionsPartitions { get; set; } = 12;

    /// <summary>How long the broker keeps positions; the raw archive keeps them for longer.</summary>
    public TimeSpan PositionsRetention { get; set; } = TimeSpan.FromHours(24);
}

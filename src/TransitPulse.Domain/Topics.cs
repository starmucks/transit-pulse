namespace TransitPulse.Domain;

/// <summary>What a Kafka topic must look like when it is created.</summary>
/// <param name="Name">Topic name.</param>
/// <param name="Partitions">Partition count: the ceiling for parallel consumers.</param>
/// <param name="Retention">How long the broker keeps messages.</param>
public sealed record TopicDefinition(string Name, int Partitions, TimeSpan Retention);

/// <summary>
/// The system's topics. Shared because several services declare the same topic: whichever starts first creates it,
/// and separate copies of these numbers could drift apart.
/// </summary>
public static class Topics
{
    /// <summary>Normalized positions, as the Ingestor publishes them.</summary>
    public static readonly TopicDefinition PositionsRaw = new("positions.raw", 12, TimeSpan.FromHours(24));

    /// <summary>Positions with the trip, the distance along the route and the delay.</summary>
    public static readonly TopicDefinition PositionsEnriched = new("positions.enriched", 12, TimeSpan.FromHours(24));

    /// <summary>Stop arrivals and departures.</summary>
    public static readonly TopicDefinition EventsStop = new("events.stop", 6, TimeSpan.FromDays(7));

    /// <summary>Bunching and other anomalies.</summary>
    public static readonly TopicDefinition EventsAnomaly = new("events.anomaly", 3, TimeSpan.FromDays(7));

    /// <summary>Messages from <see cref="PositionsRaw"/> that could not be processed.</summary>
    public static readonly TopicDefinition PositionsRawDlq = new("positions.raw.dlq", 1, TimeSpan.FromDays(7));
}

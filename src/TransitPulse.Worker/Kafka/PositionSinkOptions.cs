namespace TransitPulse.Worker.Kafka;

/// <summary>Thinning, flush cadence and Redis TTL for the position sink (<c>PositionSink</c>).</summary>
internal sealed class PositionSinkOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "PositionSink";

    /// <summary>Minimum event-time gap between warm Timescale points for one vehicle (ADR-0015).</summary>
    public TimeSpan ThinningInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Minimum event-time gap between hot Redis GEO updates for one vehicle (ADR-0018). Must be ≤
    /// <see cref="ThinningInterval"/>.
    /// </summary>
    public TimeSpan HotThinningInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often a non-empty buffer is written to Timescale/Redis. Kept short for C3; Kafka offset commits still
    /// follow <c>Kafka:CommitInterval</c>.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>TTL refreshed on each vehicle attribute hash write.</summary>
    public TimeSpan GeoAttributeTtl { get; set; } = TimeSpan.FromSeconds(120);
}

namespace TransitPulse.Worker.Kafka;

/// <summary>Thinning and Redis TTL for the position sink, configuration section <c>PositionSink</c>.</summary>
internal sealed class PositionSinkOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "PositionSink";

    /// <summary>Minimum event-time gap between warm points for one vehicle (ADR-0015).</summary>
    public TimeSpan ThinningInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>TTL refreshed on each vehicle attribute hash write.</summary>
    public TimeSpan GeoAttributeTtl { get; set; } = TimeSpan.FromSeconds(120);
}

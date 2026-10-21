namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>HSL High-Frequency Positioning (HFP) feed settings, configuration section <c>Feeds:Hsl</c>.</summary>
internal sealed class HslMqttOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Feeds:Hsl";

    /// <summary>MQTT broker host; HSL serves HFP over TLS only.</summary>
    public string Host { get; set; } = "mqtt.hsl.fi";

    /// <summary>MQTT over TLS port.</summary>
    public int Port { get; set; } = 8883;

    /// <summary>Topic filter: vehicle positions of every ongoing journey, all modes.</summary>
    public string Topic { get; set; } = "/hfp/v2/journey/ongoing/vp/#";

    /// <summary>
    /// No message for this long means the subscription is lost even if the connection still answers pings;
    /// the source then reconnects. HFP carries hundreds of messages per second even at night, so a minute
    /// of silence is a real fault.
    /// </summary>
    public TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Deadline for setting a connection up: TCP connect, TLS handshake, MQTT CONNECT and SUBSCRIBE. Without it a
    /// handshake stalled on a starved CPU or a broken network blocks the feed for good (50 min on 2026-10-21).
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

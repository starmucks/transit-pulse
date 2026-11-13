namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// One MQTT feed from the HSL High-Frequency Positioning (HFP) broker: an entry of the configuration list
/// <c>Feeds:Mqtt</c>, its own feed in the archive. It receives over one connection (<see cref="Topics"/>) or over
/// several (<see cref="Connections"/>).
/// </summary>
internal sealed class HslMqttOptions
{
    /// <summary>Feed id of every message this subscription receives, e.g. <c>hsl</c>.</summary>
    public string FeedId { get; set; } = "";

    /// <summary>MQTT broker host; HSL serves HFP over TLS only.</summary>
    public string Host { get; set; } = "mqtt.hsl.fi";

    /// <summary>MQTT over TLS port.</summary>
    public int Port { get; set; } = 8883;

    /// <summary>Topic filters of a feed received over one connection, subscribed in one SUBSCRIBE; set in configuration only.</summary>
    /// <remarks>
    /// No default on purpose: the configuration binder appends configured items to a non-empty default array
    /// instead of replacing it, which would silently subscribe to an extra topic.
    /// </remarks>
    public string[] Topics { get; set; } = [];

    /// <summary>
    /// Instead of <see cref="Topics"/>: one connection per entry, each with its own disjoint filters. Packet loss on
    /// the path caps a single TCP flow below the peak HSL stream; several flows carry it together (ADR-0012).
    /// </summary>
    public HslMqttConnectionOptions[] Connections { get; set; } = [];

    /// <summary>The topic filters of each connection, whichever way they are configured.</summary>
    public IReadOnlyList<string[]> TopicGroups => Connections.Length > 0 ? [.. Connections.Select(connection => connection.Topics)] : [Topics];

    /// <summary>
    /// No message for this long means the subscription is lost even if the connection still answers pings;
    /// the source then reconnects. Must exceed the longest normal gap between messages of the subscribed topics.
    /// </summary>
    public TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Silence after which the feed is reported silent; longer than <see cref="SilenceTimeout"/> plus a reconnect, so a
    /// report means reconnecting did not help. <see langword="null"/> (the default) turns the report off until gaps
    /// are measured in the archive.
    /// </summary>
    public TimeSpan? MaxSilence { get; set; }

    /// <summary>
    /// Deadline for setting a connection up: TCP connect, TLS handshake, MQTT CONNECT and SUBSCRIBE. Without it a
    /// handshake stalled on a starved CPU or a broken network blocks the feed for good (50 min on 2026-10-21).
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>One connection of an MQTT feed split over several: an entry of <see cref="HslMqttOptions.Connections"/>.</summary>
internal sealed class HslMqttConnectionOptions
{
    /// <summary>Topic filters of this connection, subscribed in one SUBSCRIBE; no default, as for <see cref="HslMqttOptions.Topics"/>.</summary>
    public string[] Topics { get; set; } = [];
}

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// One MQTT subscription to the HSL High-Frequency Positioning (HFP) broker: an entry of the configuration list
/// <c>Feeds:Mqtt</c>. Each entry is its own connection and its own feed in the archive.
/// </summary>
internal sealed class HslMqttOptions
{
    /// <summary>Feed id of every message this subscription receives, e.g. <c>hsl</c>.</summary>
    public string FeedId { get; set; } = "";

    /// <summary>MQTT broker host; HSL serves HFP over TLS only.</summary>
    public string Host { get; set; } = "mqtt.hsl.fi";

    /// <summary>MQTT over TLS port.</summary>
    public int Port { get; set; } = 8883;

    /// <summary>Topic filters, subscribed in one SUBSCRIBE; set in configuration only.</summary>
    /// <remarks>
    /// No default on purpose: the configuration binder appends configured items to a non-empty default array
    /// instead of replacing it, which would silently subscribe to an extra topic.
    /// </remarks>
    public string[] Topics { get; set; } = [];

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

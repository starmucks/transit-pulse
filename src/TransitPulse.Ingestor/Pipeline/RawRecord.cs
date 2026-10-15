namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// One message exactly as a feed delivered it. The raw archive stores these unchanged, so normalization
/// can be fixed later and replayed over the history.
/// </summary>
/// <param name="FeedId">Feed the message came from, e.g. <c>hsl</c>, <c>entur-sky-vm</c>.</param>
/// <param name="ReceivedAt">When the Ingestor received the message, in UTC; the source's own timestamp stays inside <paramref name="Payload"/>.</param>
/// <param name="Payload">Message body as received: JSON, XML or protobuf bytes.</param>
/// <param name="Topic">MQTT topic for push feeds (HSL encodes route, direction and next stop in it); <see langword="null"/> for polled feeds.</param>
public sealed record RawRecord(string FeedId, DateTimeOffset ReceivedAt, byte[] Payload, string? Topic);

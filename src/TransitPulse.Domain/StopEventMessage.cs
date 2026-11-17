using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransitPulse.Domain;

/// <summary>
/// Wire format of <c>events.stop</c>: the key puts all events of one line in one partition, in the order they were found,
/// the value is the event as camelCase JSON with its <see cref="VehicleStopEvent.Id"/>. Shared by the Processor that writes
/// the topic and the Worker that reads it.
/// </summary>
public static class StopEventMessage
{
    /// <summary>
    /// <c>{FeedId}/{RouteId}</c>: the feed is included because route ids are only unique within a feed. A vehicle's events
    /// stay in order too, as one trip is on one line.
    /// </summary>
    /// <param name="stopEvent">The event to publish.</param>
    public static string Key(VehicleStopEvent stopEvent) => $"{stopEvent.FeedId}/{stopEvent.RouteId}";

    /// <summary>UTF-8 JSON of <paramref name="stopEvent"/>.</summary>
    /// <param name="stopEvent">The event to publish.</param>
    public static byte[] Value(VehicleStopEvent stopEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(stopEvent, StopEventJsonContext.Default.VehicleStopEvent);

    /// <summary>Reads a value written by <see cref="Value"/>; the id is computed again, not read.</summary>
    /// <param name="value">UTF-8 JSON bytes.</param>
    /// <exception cref="JsonException">The bytes are not valid JSON of a stop event.</exception>
    public static VehicleStopEvent Parse(ReadOnlySpan<byte> value) =>
        JsonSerializer.Deserialize(value, StopEventJsonContext.Default.VehicleStopEvent)
        ?? throw new JsonException("The message value is null.");
}

/// <summary>Source-generated serializer: no runtime reflection, so it works in the Native AOT build.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VehicleStopEvent))]
internal sealed partial class StopEventJsonContext : JsonSerializerContext;

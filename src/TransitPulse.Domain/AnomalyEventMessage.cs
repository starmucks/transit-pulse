using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransitPulse.Domain;

/// <summary>Wire format of <c>events.anomaly</c>.</summary>
public static class AnomalyEventMessage
{
    /// <summary><c>{FeedId}/{RouteId}</c>, keeping one line's anomalies in order.</summary>
    /// <param name="bunching">The event to publish.</param>
    public static string Key(BunchingEvent bunching) => $"{bunching.FeedId}/{bunching.RouteId}";

    /// <summary>UTF-8 camel-case JSON of a bunching event.</summary>
    /// <param name="bunching">The event to publish.</param>
    public static byte[] Value(BunchingEvent bunching) =>
        JsonSerializer.SerializeToUtf8Bytes(bunching, AnomalyEventJsonContext.Default.BunchingEvent);

    /// <summary>Reads a bunching event written by <see cref="Value"/>.</summary>
    /// <param name="value">UTF-8 JSON bytes.</param>
    /// <exception cref="JsonException">The bytes are not valid JSON of a bunching event.</exception>
    public static BunchingEvent ParseBunching(ReadOnlySpan<byte> value) =>
        JsonSerializer.Deserialize(value, AnomalyEventJsonContext.Default.BunchingEvent)
        ?? throw new JsonException("The message value is null.");
}

/// <summary>Source-generated anomaly serializer for Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BunchingEvent))]
internal sealed partial class AnomalyEventJsonContext : JsonSerializerContext;

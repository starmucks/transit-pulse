using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransitPulse.Domain;

/// <summary>Wire format of <c>positions.enriched</c>, keyed by the same vehicle as <c>positions.raw</c>.</summary>
public static class EnrichedPositionMessage
{
    /// <summary><c>{FeedId}/{VehicleId}</c>, keeping a vehicle in the same partition as its raw positions.</summary>
    /// <param name="position">The enriched position to publish.</param>
    public static string Key(EnrichedPosition position) => PositionMessage.Key(position.Position);

    /// <summary>UTF-8 camel-case JSON of <paramref name="position"/>; null fields are left out.</summary>
    /// <param name="position">The enriched position to publish.</param>
    public static byte[] Value(EnrichedPosition position) =>
        JsonSerializer.SerializeToUtf8Bytes(position, EnrichedPositionJsonContext.Default.EnrichedPosition);

    /// <summary>Reads a value written by <see cref="Value"/>.</summary>
    /// <param name="value">UTF-8 JSON bytes.</param>
    public static EnrichedPosition Parse(ReadOnlySpan<byte> value) =>
        JsonSerializer.Deserialize(value, EnrichedPositionJsonContext.Default.EnrichedPosition)
        ?? throw new JsonException("The message value is null.");
}

/// <summary>Source-generated serializer, including the nested raw position, for Native AOT.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(EnrichedPosition))]
internal sealed partial class EnrichedPositionJsonContext : JsonSerializerContext;

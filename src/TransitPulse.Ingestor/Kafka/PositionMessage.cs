using System.Text.Json;
using System.Text.Json.Serialization;
using TransitPulse.Domain;

namespace TransitPulse.Ingestor.Kafka;

/// <summary>
/// Wire format of <c>positions.raw</c>: the key routes all positions of one vehicle to one partition (and so keeps
/// them in order), the value is the position as camelCase JSON readable in the broker console.
/// </summary>
internal static class PositionMessage
{
    /// <summary>
    /// <c>{FeedId}/{VehicleId}</c>. The feed is included because vehicle ids are only unique within a feed:
    /// a bare id would make two cities' vehicles one key.
    /// </summary>
    /// <param name="position">The position to publish.</param>
    public static string Key(VehiclePosition position) => $"{position.FeedId}/{position.VehicleId}";

    /// <summary>UTF-8 JSON of <paramref name="position"/>; unknown (null) fields are left out.</summary>
    /// <param name="position">The position to publish.</param>
    public static byte[] Value(VehiclePosition position) =>
        JsonSerializer.SerializeToUtf8Bytes(position, PositionJsonContext.Default.VehiclePosition);

    /// <summary>Reads a value written by <see cref="Value"/>.</summary>
    /// <param name="value">UTF-8 JSON bytes.</param>
    /// <exception cref="JsonException">The bytes are not valid JSON of a position.</exception>
    /// <exception cref="ArgumentException">A value breaks a rule of <see cref="VehiclePosition"/> (e.g. latitude 91).</exception>
    public static VehiclePosition Parse(ReadOnlySpan<byte> value) =>
        JsonSerializer.Deserialize(value, PositionJsonContext.Default.VehiclePosition)
        ?? throw new JsonException("The message value is null.");
}

/// <summary>Source-generated serializer: no runtime reflection, so it works in the Native AOT build.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // Names, not GTFS-RT numbers: readable in the console, and the enum's numbers stay an internal detail.
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(VehiclePosition))]
internal sealed partial class PositionJsonContext : JsonSerializerContext;

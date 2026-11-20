using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransitPulse.Domain;

/// <summary>Wire format of <c>positions.raw.dlq</c>.</summary>
public static class RawPositionDeadLetterMessage
{
    /// <summary>A deterministic source coordinate, so repeated publication has the same key.</summary>
    /// <param name="deadLetter">The failed source record.</param>
    public static string Key(RawPositionDeadLetter deadLetter) =>
        $"{deadLetter.SourceTopic}/{deadLetter.SourcePartition}/{deadLetter.SourceOffset}";

    /// <summary>UTF-8 camel-case JSON containing the source record and its failure.</summary>
    /// <param name="deadLetter">The failed source record.</param>
    public static byte[] Value(RawPositionDeadLetter deadLetter) =>
        JsonSerializer.SerializeToUtf8Bytes(deadLetter, RawPositionDeadLetterJsonContext.Default.RawPositionDeadLetter);

    /// <summary>Reads a dead letter written by <see cref="Value"/>.</summary>
    /// <param name="value">UTF-8 JSON bytes.</param>
    /// <exception cref="JsonException">The bytes are not valid JSON of a dead letter.</exception>
    public static RawPositionDeadLetter Parse(ReadOnlySpan<byte> value) =>
        JsonSerializer.Deserialize(value, RawPositionDeadLetterJsonContext.Default.RawPositionDeadLetter)
        ?? throw new JsonException("The message value is null.");
}

/// <summary>Source-generated dead-letter serializer for Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RawPositionDeadLetter))]
internal sealed partial class RawPositionDeadLetterJsonContext : JsonSerializerContext;

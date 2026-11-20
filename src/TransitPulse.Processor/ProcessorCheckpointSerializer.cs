using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransitPulse.Processor;

internal static class ProcessorCheckpointSerializer
{
    public static byte[] Serialize(ProcessorCheckpointSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, ProcessorCheckpointJsonContext.Default.ProcessorCheckpointSnapshot);

    public static ProcessorCheckpointSnapshot Deserialize(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize(payload, ProcessorCheckpointJsonContext.Default.ProcessorCheckpointSnapshot);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProcessorCheckpointSnapshot))]
internal sealed partial class ProcessorCheckpointJsonContext : JsonSerializerContext;

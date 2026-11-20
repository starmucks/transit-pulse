using System.Text;

namespace TransitPulse.Domain.Tests;

public sealed class RawPositionDeadLetterMessageTests
{
    private static readonly RawPositionDeadLetter _deadLetter = new(
        "positions.raw",
        2,
        41,
        "hsl/40/415",
        "not json"u8.ToArray(),
        3,
        "JsonException",
        "The message is not valid JSON.");

    [Fact]
    public void Key_is_the_source_coordinate()
    {
        Assert.Equal("positions.raw/2/41", RawPositionDeadLetterMessage.Key(_deadLetter));
    }

    [Fact]
    public void Value_contains_the_original_record_and_failure()
    {
        var json = Encoding.UTF8.GetString(RawPositionDeadLetterMessage.Value(_deadLetter));

        Assert.Equal(
            """{"sourceKey":"hsl/40/415","sourceTopic":"positions.raw","sourcePartition":2,"sourceOffset":41,"sourceValue":"bm90IGpzb24=","attempts":3,"errorType":"JsonException","reason":"The message is not valid JSON."}""",
            json);
    }

    [Fact]
    public void Value_reads_back_to_the_same_fields()
    {
        var restored = RawPositionDeadLetterMessage.Parse(RawPositionDeadLetterMessage.Value(_deadLetter));

        Assert.Equal(_deadLetter.SourceTopic, restored.SourceTopic);
        Assert.Equal(_deadLetter.SourcePartition, restored.SourcePartition);
        Assert.Equal(_deadLetter.SourceOffset, restored.SourceOffset);
        Assert.Equal(_deadLetter.SourceKey, restored.SourceKey);
        Assert.Equal(_deadLetter.SourceValue, restored.SourceValue);
        Assert.Equal(_deadLetter.Attempts, restored.Attempts);
        Assert.Equal(_deadLetter.ErrorType, restored.ErrorType);
        Assert.Equal(_deadLetter.Reason, restored.Reason);
    }
}

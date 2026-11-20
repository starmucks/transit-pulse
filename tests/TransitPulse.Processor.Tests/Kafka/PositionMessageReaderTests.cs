using System.Text.Json;
using TransitPulse.Domain;
using TransitPulse.Processor.Kafka;

namespace TransitPulse.Processor.Tests.Kafka;

public sealed class PositionMessageReaderTests
{
    private readonly PositionMessageReader _reader = new();

    [Fact]
    public void Reads_a_valid_position()
    {
        var position = new VehiclePosition(
            "hsl", "40/415", null, "1009", 60.2, 24.9, null, null, null, null,
            new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(position, _reader.Read("hsl/40/415", PositionMessage.Value(position)));
    }

    [Fact]
    public void Missing_key_is_a_typed_message_failure()
    {
        var error = Assert.Throws<PositionMessageException>(() => _reader.Read(null, []));

        Assert.Equal("MissingKey", error.ErrorType);
        Assert.Equal("The message key is missing.", error.Message);
    }

    [Fact]
    public void Invalid_json_preserves_the_public_error_type_and_reason()
    {
        var error = Assert.Throws<PositionMessageException>(() => _reader.Read("hsl/40/415", "not json"u8));

        Assert.Equal(nameof(JsonException), error.ErrorType);
        Assert.IsType<JsonException>(error.InnerException);
        Assert.NotEmpty(error.Message);
    }
}

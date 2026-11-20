using System.Text.Json;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Kafka;

internal interface IPositionMessageReader
{
    VehiclePosition Read(string? key, ReadOnlySpan<byte> value);
}

internal sealed class PositionMessageReader : IPositionMessageReader
{
    public VehiclePosition Read(string? key, ReadOnlySpan<byte> value)
    {
        if (key is null)
        {
            throw new PositionMessageException("MissingKey", "The message key is missing.");
        }

        try
        {
            return PositionMessage.Parse(value);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new PositionMessageException(ex.GetType().Name, ex.Message, ex);
        }
    }
}

internal sealed class PositionMessageException : Exception
{
    public PositionMessageException(string errorType, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        ErrorType = errorType;
    }

    public string ErrorType { get; }
}

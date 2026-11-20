namespace TransitPulse.Domain;

/// <summary>A <c>positions.raw</c> record that remained unreadable after the configured processing attempts.</summary>
/// <param name="SourceTopic">Topic the original record came from.</param>
/// <param name="SourcePartition">Partition the original record came from.</param>
/// <param name="SourceOffset">Offset of the original record.</param>
/// <param name="SourceKey">Original Kafka key, if it had one.</param>
/// <param name="SourceValue">Original Kafka value.</param>
/// <param name="Attempts">Number of processing attempts made.</param>
/// <param name="ErrorType">Stable exception type name for grouping failures.</param>
/// <param name="Reason">Human-readable failure reason without a stack trace.</param>
public sealed record RawPositionDeadLetter(
    string SourceTopic,
    int SourcePartition,
    long SourceOffset,
    string? SourceKey,
    byte[] SourceValue,
    int Attempts,
    string ErrorType,
    string Reason)
{
    /// <summary>Topic the original record came from.</summary>
    public string SourceTopic { get; } = RequireText(SourceTopic);

    /// <summary>Partition the original record came from.</summary>
    public int SourcePartition { get; } = SourcePartition >= 0
        ? SourcePartition
        : throw new ArgumentOutOfRangeException(nameof(SourcePartition));

    /// <summary>Offset of the original record.</summary>
    public long SourceOffset { get; } = SourceOffset >= 0
        ? SourceOffset
        : throw new ArgumentOutOfRangeException(nameof(SourceOffset));

    /// <summary>Original Kafka value.</summary>
    public byte[] SourceValue { get; } = SourceValue ?? throw new ArgumentNullException(nameof(SourceValue));

    /// <summary>Number of processing attempts made.</summary>
    public int Attempts { get; } = Attempts > 0
        ? Attempts
        : throw new ArgumentOutOfRangeException(nameof(Attempts));

    /// <summary>Stable exception type name for grouping failures.</summary>
    public string ErrorType { get; } = RequireText(ErrorType);

    /// <summary>Human-readable failure reason without a stack trace.</summary>
    public string Reason { get; } = RequireText(Reason);

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

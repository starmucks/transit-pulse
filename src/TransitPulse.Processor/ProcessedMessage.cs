using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>What <see cref="PartitionState.Process"/> found out about a message.</summary>
/// <param name="Verdict">Whether the position goes on, or why it was dropped.</param>
/// <param name="Trip">The trip lookup of an accepted position; <see langword="null"/> if none was made.</param>
/// <param name="Route">Where the position lies on its found trip's line; <see langword="null"/> when no trip was found.</param>
/// <param name="Position">The output value for an accepted position.</param>
/// <param name="Delay">A delay check completed by an arrival, if any.</param>
internal readonly record struct ProcessedMessage(
    MessageVerdict Verdict,
    TripResolution? Trip,
    RouteMatch? Route = null,
    EnrichedPosition? Position = null,
    DelayCheck? Delay = null);

/// <summary>Why an actual arrival could or could not be compared with the source's delay.</summary>
internal enum DelayCheckResult
{
    Compared,
    NoSourceDelay,
    NoScheduledArrival,
}

/// <summary>One actual-arrival delay check.</summary>
/// <param name="Result">Whether both values existed.</param>
/// <param name="CalculatedSec">Actual arrival minus scheduled arrival, when the schedule gave a time.</param>
/// <param name="SourceSec">The source delay at the position nearest the actual arrival, when supplied.</param>
internal readonly record struct DelayCheck(DelayCheckResult Result, int? CalculatedSec = null, int? SourceSec = null);

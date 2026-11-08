using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>What <see cref="PartitionState.Process"/> found out about a message.</summary>
/// <param name="Verdict">Whether the position goes on, or why it was dropped.</param>
/// <param name="Trip">The trip lookup of an accepted position; <see langword="null"/> if none was made.</param>
/// <param name="Route">Where the position lies on its found trip's line; <see langword="null"/> when no trip was found.</param>
internal readonly record struct ProcessedMessage(MessageVerdict Verdict, TripResolution? Trip, RouteMatch? Route = null);

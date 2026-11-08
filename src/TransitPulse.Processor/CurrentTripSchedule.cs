using TransitPulse.Domain;

namespace TransitPulse.Processor;

/// <summary>
/// The schedule the loop looks trips up in. A background service builds a new one and swaps it in whole, so the loop never
/// waits for the database and never sees a half-built schedule (ADR-0010).
/// </summary>
/// <param name="feedId">The only feed with a schedule so far; other cities join in stage 4.</param>
internal sealed class CurrentTripSchedule(string feedId)
{
    // Written by the background service, read by the loop.
    private TripSchedule? _schedule;

    /// <summary>Makes a new schedule the current one; a message already being handled finishes with the old one.</summary>
    /// <param name="schedule">The new schedule.</param>
    public void Replace(TripSchedule schedule) => Volatile.Write(ref _schedule, schedule);

    /// <summary>Looks up a position's trip.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The result, or <see langword="null"/> for a feed without a schedule: its trip ids mean something else.</returns>
    public TripResolution? Resolve(VehiclePosition position)
    {
        if (!string.Equals(position.FeedId, feedId, StringComparison.Ordinal))
        {
            return null;
        }

        return Volatile.Read(ref _schedule) is { } schedule ? schedule.Find(position.TripId, out _) : TripResolution.NoSchedule;
    }
}

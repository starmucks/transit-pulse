namespace TransitPulse.Domain;

/// <summary>Computes how early or late an observed stop event is against its GTFS service-day time.</summary>
public static class ScheduleDeviation
{
    /// <summary>Returns actual time minus scheduled time in whole seconds: positive when late, negative when early.</summary>
    /// <param name="serviceDate">The local service day of the trip.</param>
    /// <param name="scheduledSeconds">Seconds after the start of the service day; may exceed 86400 after midnight.</param>
    /// <param name="actual">The observed event time.</param>
    /// <param name="timeZone">The schedule feed's local time zone.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scheduledSeconds"/> is negative.</exception>
    public static int InSeconds(
        DateOnly serviceDate, int scheduledSeconds, DateTimeOffset actual, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentOutOfRangeException.ThrowIfNegative(scheduledSeconds);

        // GTFS Time is elapsed from local noon minus twelve hours, not from local midnight: on a daylight-saving
        // transition day that distinction keeps the time line continuous and avoids ambiguous or nonexistent wall times.
        var localNoon = DateTime.SpecifyKind(serviceDate.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        var serviceDayStart = TimeZoneInfo.ConvertTimeToUtc(localNoon, timeZone).AddHours(-12);
        var scheduled = serviceDayStart.AddSeconds(scheduledSeconds);
        return checked((int)Math.Round((actual.UtcDateTime - scheduled).TotalSeconds, MidpointRounding.AwayFromZero));
    }
}

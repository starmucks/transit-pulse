namespace TransitPulse.Processor.Schedule;

/// <summary>Where the schedule comes from and which days it holds, configuration section <c>Schedule</c>.</summary>
internal sealed class TripScheduleOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Schedule";

    /// <summary>The feed with a schedule; positions of other feeds make no lookup.</summary>
    public string FeedId { get; set; } = "hsl";

    /// <summary>The feed's time zone: its "today" decides which service days are held.</summary>
    public string TimeZone { get; set; } = "Europe/Helsinki";

    /// <summary>How often to check for a new day or snapshot, and to retry after a failed read.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long the start waits for the first read before the stream begins without a schedule; the read goes on.
    /// </summary>
    public TimeSpan StartupWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A fixed "today" for replaying an old archive hour, whose positions would otherwise fall outside the service days of
    /// the real today. Unset in normal work.
    /// </summary>
    public DateOnly? ServiceDate { get; set; }
}

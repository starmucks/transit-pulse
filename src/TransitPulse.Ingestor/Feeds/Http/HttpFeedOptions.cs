namespace TransitPulse.Ingestor.Feeds.Http;

/// <summary>Settings of one feed fetched by periodic HTTP GET.</summary>
internal sealed class HttpFeedOptions
{
    /// <summary>Feed identifier written into every record, e.g. <c>mbta-vp</c>.</summary>
    public string FeedId { get; set; } = "";

    /// <summary>Absolute URL to fetch.</summary>
    public string Url { get; set; } = "";

    /// <summary>Time between the starts of two polls.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A poll that takes longer is abandoned and counted as failed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest normal time without a new response; longer marks the feed silent. Unset for feeds whose content may
    /// stay unchanged for long (alerts, weather): unchanged responses are not archived, so silence is normal there.
    /// </summary>
    public TimeSpan? MaxSilence { get; set; }

    /// <summary>
    /// Largest accepted response body. The whole body is held in memory once per poll, so this bounds what a
    /// broken or unexpectedly large response can take from the collector's memory limit.
    /// </summary>
    public long MaxResponseBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Headers sent with every request, e.g. the client name some providers require.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
}

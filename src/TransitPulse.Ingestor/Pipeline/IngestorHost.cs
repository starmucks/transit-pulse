using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Feeds.Http;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Replay;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>Host wiring shared by the process entry point and registration tests.</summary>
internal static class IngestorHost
{
    /// <summary>
    /// Registers feeds, sinks, the pipeline and feed-silence monitoring the same way <c>Program</c> does after the
    /// archive self-test and profile CLI commands.
    /// </summary>
    /// <returns>The configured <see cref="IngestorMode"/> (needed after the host is built, for replay).</returns>
    public static IngestorMode Configure(HostApplicationBuilder builder)
    {
        builder.AddServiceDefaults();

        var mode = RawSinkRegistration.ReadMode(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddRawSink(builder.Configuration);
        if (mode == IngestorMode.Replay)
        {
            builder.Services.AddReplay();
        }
        else
        {
            builder.Services.AddHslMqttFeeds(builder.Configuration);
            builder.Services.AddHttpFeeds(builder.Configuration);
        }

        builder.Services.AddSingleton<FeedMetrics>();
        if (mode != IngestorMode.Archive)
        {
            // Before the pipeline: hosted services stop in reverse order, so the totals come after the last record is published.
            builder.Services.AddHostedService<RunSummary>();
        }

        builder.Services.AddHostedService<IngestionPipeline>();

        // Run in process, not served over HTTP: the collector has no web server, and memory is its tightest limit.
        builder.Services.AddFeedSilenceMonitoring();

        // Covers the pipeline's drain budget plus closing every open hour on a slow CPU; an unclosed file loses its
        // unflushed records. The service manager's stop timeout must be longer than this.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = IngestionPipeline.DrainTimeout + TimeSpan.FromSeconds(80));

        return mode;
    }
}

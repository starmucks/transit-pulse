using Microsoft.Extensions.Diagnostics.HealthChecks;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Feeds.Http;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Replay;

// Checked before the host is built: the self-test needs no configuration, telemetry or feeds.
if (args.Contains("--self-test"))
{
    return await ArchiveSelfTest.RunAsync();
}

if (args is ["--repair", var repairInput, var repairOutput])
{
    return await ArchiveRepair.RunAsync(repairInput, repairOutput, Console.Out);
}

if (args is ["--stats", var statsInput])
{
    return await ArchiveStats.RunAsync(statsInput, Console.Out);
}

var builder = Host.CreateApplicationBuilder(args);

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
    builder.Services.AddOptions<HslMqttOptions>().BindConfiguration(HslMqttOptions.SectionName)
        // A limit within the reconnect timeout would report every reconnect as a silent feed.
        .Validate(o => o.MaxSilence is null || o.MaxSilence > o.SilenceTimeout, "Feeds:Hsl:MaxSilence must be longer than SilenceTimeout.")
        .ValidateOnStart();
    builder.Services.AddSingleton<IFeedSource, HslMqttSource>();
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
builder.Services.AddHealthChecks().AddCheck<FeedSilenceHealthCheck>(FeedSilenceHealthCheck.Name);
builder.Services.AddSingleton<IHealthCheckPublisher, FeedSilenceLog>();

// Covers the pipeline's drain budget plus closing every open hour on a slow CPU; an unclosed file loses its unflushed
// records. The service manager's stop timeout must be longer than this.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = IngestionPipeline.DrainTimeout + TimeSpan.FromSeconds(80));

using var host = builder.Build();

// Resolved before running: the host disposes its services when it stops. A failed replay stops the host like a
// finished one, so only its flag tells the two apart.
var replay = mode == IngestorMode.Replay ? host.Services.GetRequiredService<ReplaySource>() : null;
var exitCode = await IngestionPipeline.RunHostAsync(host);
return replay is { Failed: true } ? 1 : exitCode;

using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Feeds.Http;
using TransitPulse.Ingestor.Pipeline;

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

// Fails at startup, not at the first hourly rotation, when the archive is not configured.
builder.Services.AddOptions<ArchiveOptions>()
    .BindConfiguration(ArchiveOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.RootDirectory), "Archive:RootDirectory is required.")
    .Validate(o => o.RowGroupMaxBytes > 0, "Archive:RowGroupMaxBytes must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRawSink>(services =>
{
    var options = services.GetRequiredService<IOptions<ArchiveOptions>>().Value;
    return new ArchiveSink(options.RootDirectory, options.RowGroupMaxBytes, services.GetRequiredService<ILogger<ArchiveSink>>());
});
builder.Services.AddOptions<HslMqttOptions>().BindConfiguration(HslMqttOptions.SectionName);
builder.Services.AddSingleton<IFeedSource, HslMqttSource>();
builder.Services.AddHttpFeeds(builder.Configuration);

builder.Services.AddHostedService<IngestionPipeline>();

// Covers the pipeline's drain budget plus closing every open hour on a slow CPU; an unclosed file loses its unflushed
// records. The service manager's stop timeout must be longer than this.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = IngestionPipeline.DrainTimeout + TimeSpan.FromSeconds(80));

using var host = builder.Build();
return await IngestionPipeline.RunHostAsync(host);

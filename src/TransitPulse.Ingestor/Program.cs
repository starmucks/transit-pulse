using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

// Checked before the host is built: the self-test needs no configuration, telemetry or feeds.
if (args.Contains("--self-test"))
{
    return await ArchiveSelfTest.RunAsync();
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
builder.Services.AddHostedService<IngestionPipeline>();

// The default 30 s may not cover draining the channel and closing every open hour; an unclosed file is lost.
// The service manager's stop timeout must be longer than this.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));

using var host = builder.Build();
return await IngestionPipeline.RunHostAsync(host);

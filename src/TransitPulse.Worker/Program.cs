using Npgsql;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Infrastructure.StopEvents;
using TransitPulse.Worker.Gtfs;
using TransitPulse.Worker.Kafka;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
// Connection string ConnectionStrings:transitpulse, given by the AppHost; adds Npgsql health check, traces and metrics.
builder.AddNpgsqlDataSource("transitpulse");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<GtfsImportOptions>()
    .BindConfiguration(GtfsImportOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.SnapshotDirectory), "GtfsImport:SnapshotDirectory is required.")
    .Validate(o => o.Interval > TimeSpan.Zero, "GtfsImport:Interval must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<WorkerKafkaOptions>()
    .BindConfiguration(WorkerKafkaOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
    .Validate(o => o.CommitInterval > TimeSpan.Zero, "Kafka:CommitInterval must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton(provider => new GtfsImporter(provider.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<StopEventStore>();
builder.Services.AddHostedService<GtfsImportService>();
builder.Services.AddHostedService<StopEventSink>();

await builder.Build().RunAsync();

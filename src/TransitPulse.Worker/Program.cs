using Npgsql;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Infrastructure.Positions;
using TransitPulse.Infrastructure.StopEvents;
using TransitPulse.Worker;
using TransitPulse.Worker.Gtfs;
using TransitPulse.Worker.Kafka;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
// Connection string ConnectionStrings:transitpulse, given by the AppHost; adds Npgsql health check, traces and metrics.
builder.AddNpgsqlDataSource("transitpulse");
// Redis resource named by the AppHost; registers the shared client, health check and telemetry.
builder.AddRedisClient("redis");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<GtfsImportOptions>()
    .BindConfiguration(GtfsImportOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.SnapshotDirectory), "GtfsImport:SnapshotDirectory is required.")
    .Validate(o => o.Interval > TimeSpan.Zero, "GtfsImport:Interval must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<WorkerKafkaOptions>()
    .BindConfiguration(WorkerKafkaOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.PositionsGroupId), "Kafka:PositionsGroupId is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.PositionsTopic), "Kafka:PositionsTopic is required.")
    .Validate(o => o.CommitInterval > TimeSpan.Zero, "Kafka:CommitInterval must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<PositionSinkOptions>()
    .BindConfiguration(PositionSinkOptions.SectionName)
    .Validate(o => o.ThinningInterval > TimeSpan.Zero, "PositionSink:ThinningInterval must be positive.")
    .Validate(o => o.GeoAttributeTtl > TimeSpan.Zero, "PositionSink:GeoAttributeTtl must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton(provider => new GtfsImporter(provider.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<StopEventStore>();
builder.Services.AddSingleton<VehiclePositionStore>();
builder.Services.AddSingleton<PositionSinkMetrics>();
builder.Services.AddHostedService<GtfsImportService>();
builder.Services.AddHostedService<StopEventSink>();
builder.Services.AddHostedService<PositionSink>();

await builder.Build().RunAsync();

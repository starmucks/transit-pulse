using Npgsql;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Worker.Gtfs;

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
builder.Services.AddSingleton(provider => new GtfsImporter(provider.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddHostedService<GtfsImportService>();

await builder.Build().RunAsync();

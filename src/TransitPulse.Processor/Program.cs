using Microsoft.Extensions.Options;
using Npgsql;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Processor;
using TransitPulse.Processor.Kafka;
using TransitPulse.Processor.Schedule;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
// Connection string ConnectionStrings:transitpulse, given by the AppHost; adds Npgsql health check, traces and metrics.
builder.AddNpgsqlDataSource("transitpulse");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ProcessorKafkaOptions>()
    .BindConfiguration(ProcessorKafkaOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
    .Validate(o => o.CommitInterval > TimeSpan.Zero, "Kafka:CommitInterval must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<TripScheduleOptions>()
    .BindConfiguration(TripScheduleOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.FeedId), "Schedule:FeedId is required.")
    .Validate(o => o.RefreshInterval > TimeSpan.Zero, "Schedule:RefreshInterval must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton(provider => new TripScheduleReader(provider.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(provider => new CurrentTripSchedule(provider.GetRequiredService<IOptions<TripScheduleOptions>>().Value.FeedId));
builder.Services.AddSingleton<ProcessorMetrics>();
// In this order: hosted services start one after another, so the loop finds its topics and its schedule in place.
builder.Services.AddHostedService<ProcessorTopicInitializer>();
builder.Services.AddHostedService<TripScheduleRefresher>();
builder.Services.AddHostedService<PositionConsumer>();

using var host = builder.Build();

// Resolved before running: the host disposes its services when it stops.
var consumer = host.Services.GetServices<IHostedService>().OfType<PositionConsumer>().Single();
await host.RunAsync();

// A failed loop stops the host, which would otherwise exit with 0: the service manager would see a clean stop and not restart it.
return consumer.ExecuteTask is { IsFaulted: true } ? 1 : 0;

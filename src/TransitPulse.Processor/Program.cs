using TransitPulse.Processor;
using TransitPulse.Processor.Kafka;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ProcessorKafkaOptions>()
    .BindConfiguration(ProcessorKafkaOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
    .Validate(o => o.CommitInterval > TimeSpan.Zero, "Kafka:CommitInterval must be positive.")
    .ValidateOnStart();
// In this order: hosted services start one after another, so the loop finds its topics in place.
builder.Services.AddSingleton<ProcessorMetrics>();
builder.Services.AddHostedService<ProcessorTopicInitializer>();
builder.Services.AddHostedService<PositionConsumer>();

using var host = builder.Build();

// Resolved before running: the host disposes its services when it stops.
var consumer = host.Services.GetServices<IHostedService>().OfType<PositionConsumer>().Single();
await host.RunAsync();

// A failed loop stops the host, which would otherwise exit with 0: the service manager would see a clean stop and not restart it.
return consumer.ExecuteTask is { IsFaulted: true } ? 1 : 0;

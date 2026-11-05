using TransitPulse.Processor.Kafka;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOptions<ProcessorKafkaOptions>()
    .BindConfiguration(ProcessorKafkaOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
    .ValidateOnStart();
builder.Services.AddHostedService<ProcessorTopicInitializer>();

await builder.Build().RunAsync();

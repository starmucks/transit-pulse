using Confluent.Kafka;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Feeds.Entur;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Kafka;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Registers where raw records go, by mode (configuration key <c>Ingestor:Mode</c>): <c>archive</c> (the default,
/// used by the collector server) writes only the raw archive; <c>full</c> also normalizes records and publishes
/// positions to Kafka through <see cref="NormalizingSink"/>.
/// </summary>
internal static class RawSinkRegistration
{
    /// <summary>Configuration key selecting the mode.</summary>
    public const string ModeKey = "Ingestor:Mode";

    /// <summary>Registers the archive and, in <c>full</c> mode, normalization and publishing.</summary>
    /// <param name="services">Service collection to add to; register this before the pipeline, so the topic exists first.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">The mode is neither <c>archive</c> nor <c>full</c>.</exception>
    public static IServiceCollection AddRawSink(this IServiceCollection services, IConfiguration configuration)
    {
        // Read as a string: binding an enum from configuration relies on reflection the AOT build may trim.
        var full = configuration[ModeKey]?.ToUpperInvariant() switch
        {
            null or "" or "ARCHIVE" => false,
            "FULL" => true,
            _ => throw new InvalidOperationException($"{ModeKey} '{configuration[ModeKey]}' is unknown; use 'archive' or 'full'."),
        };

        // Fails at startup, not at the first hourly rotation, when the archive is not configured.
        services.AddOptions<ArchiveOptions>()
            .BindConfiguration(ArchiveOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.RootDirectory), "Archive:RootDirectory is required.")
            .Validate(o => o.RowGroupMaxBytes > 0, "Archive:RowGroupMaxBytes must be positive.")
            .ValidateOnStart();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ArchiveOptions>>().Value;
            return new ArchiveSink(options.RootDirectory, options.RowGroupMaxBytes, provider.GetRequiredService<ILogger<ArchiveSink>>());
        });

        if (!full)
        {
            return services.AddSingleton<IRawSink>(provider => provider.GetRequiredService<ArchiveSink>());
        }

        services.AddOptions<KafkaOptions>()
            .BindConfiguration(KafkaOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required in full mode.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.PositionsTopic), "Kafka:PositionsTopic is required.")
            .Validate(o => o.PositionsPartitions > 0, "Kafka:PositionsPartitions must be positive.")
            // retention.ms=0 would let the broker delete positions almost as soon as they arrive.
            .Validate(o => o.PositionsRetention > TimeSpan.Zero, "Kafka:PositionsRetention must be positive.")
            .ValidateOnStart();
        services.AddHostedService<KafkaTopicInitializer>();
        // One normalizer per city; the router finds each by the raw feeds it claims.
        services.AddSingleton<IPositionNormalizer, HslPositionNormalizer>();
        services.AddSingleton<IPositionNormalizer, SiriVmPositionNormalizer>();
        services.AddSingleton<PositionRouter>();
        services.AddSingleton<IPositionSink>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<KafkaOptions>>().Value;
            return new KafkaSink(
                new ProducerConfig { BootstrapServers = options.BootstrapServers, ClientId = "transitpulse-ingestor" },
                options.PositionsTopic,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<KafkaSink>>());
        });
        return services.AddSingleton<IRawSink>(provider => new NormalizingSink(
            provider.GetRequiredService<ArchiveSink>(),
            provider.GetRequiredService<PositionRouter>(),
            provider.GetRequiredService<IPositionSink>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<NormalizingSink>>()));
    }
}

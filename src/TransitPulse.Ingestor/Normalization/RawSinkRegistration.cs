using Confluent.Kafka;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Feeds.Entur;
using TransitPulse.Ingestor.Feeds.GtfsRt;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Kafka;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>How the Ingestor runs, configuration key <c>Ingestor:Mode</c>.</summary>
internal enum IngestorMode
{
    /// <summary>The default, used by the collector server: live feeds into the raw archive only.</summary>
    Archive,

    /// <summary>Live feeds into the archive, also normalized and published to Kafka.</summary>
    Full,

    /// <summary>Archived records replayed through normalization to Kafka; nothing is archived again.</summary>
    Replay,
}

/// <summary>
/// Registers where raw records go, by <see cref="IngestorMode"/>: <c>archive</c> writes only the raw archive;
/// <c>full</c> also normalizes records and publishes positions to Kafka through <see cref="NormalizingSink"/>;
/// <c>replay</c> normalizes and publishes the same way but writes no archive.
/// </summary>
internal static class RawSinkRegistration
{
    /// <summary>Configuration key selecting the mode.</summary>
    public const string ModeKey = "Ingestor:Mode";

    /// <summary>Reads the mode; a missing setting means <see cref="IngestorMode.Archive"/>.</summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The configured mode.</returns>
    /// <exception cref="InvalidOperationException">The mode is not <c>archive</c>, <c>full</c> or <c>replay</c>.</exception>
    public static IngestorMode ReadMode(IConfiguration configuration) =>
        // Read as a string: binding an enum from configuration relies on reflection the AOT build may trim.
        configuration[ModeKey]?.ToUpperInvariant() switch
        {
            null or "" or "ARCHIVE" => IngestorMode.Archive,
            "FULL" => IngestorMode.Full,
            "REPLAY" => IngestorMode.Replay,
            _ => throw new InvalidOperationException($"{ModeKey} '{configuration[ModeKey]}' is unknown; use 'archive', 'full' or 'replay'."),
        };

    /// <summary>Registers the archive and, in <c>full</c> and <c>replay</c> modes, normalization and publishing.</summary>
    /// <param name="services">Service collection to add to; register this before the pipeline, so the topic exists first.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">The mode is unknown.</exception>
    public static IServiceCollection AddRawSink(this IServiceCollection services, IConfiguration configuration)
    {
        var mode = ReadMode(configuration);

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

        if (mode == IngestorMode.Archive)
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
        // Boston: the GTFS-RT adapter is city-neutral, so a city is its pair of feed ids.
        services.AddSingleton<IPositionNormalizer>(new GtfsRtPositionNormalizer(feedId: "mbta", rawFeedId: "mbta-vp"));
        // The host registers metrics already; repeated here (a no-op then) so this registration stands on its own.
        services.AddMetrics();
        services.AddSingleton<PositionMetrics>();
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
            // A replay written back would land next to the files it reads: the archive lays files out by receive time.
            mode == IngestorMode.Replay ? NoArchive.Instance : provider.GetRequiredService<ArchiveSink>(),
            provider.GetRequiredService<PositionRouter>(),
            provider.GetRequiredService<IPositionSink>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<PositionMetrics>(),
            provider.GetRequiredService<ILogger<NormalizingSink>>(),
            waitWhenFull: mode == IngestorMode.Replay));
    }

    /// <summary>Stands in for the archive in replay mode: the records come from the archive already.</summary>
    private sealed class NoArchive : IRawSink
    {
        public static readonly NoArchive Instance = new();

        public ValueTask WriteAsync(RawRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

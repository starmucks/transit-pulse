using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Kafka;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Feeds.Entur;

namespace TransitPulse.Ingestor.Tests.Normalization;

public sealed class RawSinkRegistrationTests
{
    [Fact]
    public void Shipped_configuration_runs_in_archive_mode()
    {
        // The collector server runs with this file: it must never start publishing by accident.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        using var provider = Build(configuration);

        Assert.IsType<ArchiveSink>(provider.GetRequiredService<IRawSink>());
        Assert.Null(provider.GetService<IPositionSink>());
        Assert.Empty(provider.GetServices<IPositionNormalizer>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), service => service is KafkaTopicInitializer);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("Full")]
    public void Full_mode_branches_the_archive_off_to_kafka(string mode)
    {
        using var provider = Build(Configuration(mode, bootstrapServers: "127.0.0.1:1"));

        Assert.IsType<NormalizingSink>(provider.GetRequiredService<IRawSink>());
        Assert.IsType<KafkaSink>(provider.GetRequiredService<IPositionSink>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is KafkaTopicInitializer);
    }

    [Fact]
    public void Full_mode_normalizes_hsl_records()
    {
        using var provider = Build(Configuration("full", bootstrapServers: "127.0.0.1:1"));
        var payload = """{"VP":{"oper":40,"veh":108,"tst":"2026-10-22T06:59:59.952Z","lat":60.167646,"long":24.952242}}"""u8.ToArray();

        var receivedAt = new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);

        var positions = provider.GetRequiredService<PositionRouter>().Route(new RawRecord("hsl", receivedAt, payload, null));

        Assert.Equal("40/108", Assert.Single(positions).VehicleId);
    }

    [Fact]
    public void Full_mode_normalizes_siri_vm_records()
    {
        using var provider = Build(Configuration("full", bootstrapServers: "127.0.0.1:1"));
        var payload = Encoding.UTF8.GetBytes(SiriVmSamples.Response("""
            <VehicleActivity><RecordedAtTime>2026-10-23T08:59:50+02:00</RecordedAtTime><MonitoredVehicleJourney>
            <VehicleLocation><Longitude>5.3</Longitude><Latitude>60.4</Latitude></VehicleLocation>
            <VehicleRef>3350447527</VehicleRef></MonitoredVehicleJourney></VehicleActivity>
            """));
        var receivedAt = new DateTimeOffset(2026, 10, 23, 7, 0, 0, TimeSpan.Zero);

        var positions = provider.GetRequiredService<PositionRouter>().Route(new RawRecord("entur-sky-vm", receivedAt, payload, null));

        Assert.Equal(("entur-sky", "3350447527"), (Assert.Single(positions).FeedId, positions[0].VehicleId));
    }

    [Fact]
    public void Full_mode_normalizes_mbta_records()
    {
        using var provider = Build(Configuration("full", bootstrapServers: "127.0.0.1:1"));
        var payload = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mbta", "vp.pb"));
        var receivedAt = new DateTimeOffset(2026, 10, 26, 0, 0, 3, TimeSpan.Zero);

        var positions = provider.GetRequiredService<PositionRouter>().Route(new RawRecord("mbta-vp", receivedAt, payload, null));

        // 10 vehicles in the snapshot, one of them stale.
        Assert.Equal(9, positions.Count);
        Assert.All(positions, position => Assert.Equal("mbta", position.FeedId));
    }

    [Theory]
    [InlineData("Kafka:BootstrapServers", "")]
    [InlineData("Kafka:PositionsTopic", " ")]
    [InlineData("Kafka:PositionsPartitions", "0")]
    [InlineData("Kafka:PositionsRetention", "00:00:00")]
    public void Invalid_kafka_setting_fails_validation(string key, string value)
    {
        using var provider = Build(Configuration("full", bootstrapServers: "127.0.0.1:1", (key, value)));

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<KafkaOptions>>().Value);

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_mode_fails_registration()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRawSink(Configuration("replay", null)));

        Assert.Contains("'replay'", error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(string mode, string? bootstrapServers, (string Key, string Value)? setting = null)
    {
        var values = new Dictionary<string, string?>
        {
            [RawSinkRegistration.ModeKey] = mode,
            ["Archive:RootDirectory"] = Path.Combine(Path.GetTempPath(), "tp-registration"),
            ["Kafka:BootstrapServers"] = bootstrapServers,
        };
        if (setting is var (key, value))
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider Build(IConfiguration configuration) =>
        new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddRawSink(configuration)
            .BuildServiceProvider();
}

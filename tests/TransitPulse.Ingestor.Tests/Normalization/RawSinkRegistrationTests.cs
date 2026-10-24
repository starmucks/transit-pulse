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
    public void Full_mode_without_a_broker_address_fails_validation()
    {
        using var provider = Build(Configuration("full", bootstrapServers: null));

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<KafkaOptions>>().Value);

        Assert.Contains("Kafka:BootstrapServers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_mode_fails_registration()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRawSink(Configuration("replay", null)));

        Assert.Contains("'replay'", error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(string mode, string? bootstrapServers) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RawSinkRegistration.ModeKey] = mode,
                ["Archive:RootDirectory"] = Path.Combine(Path.GetTempPath(), "tp-registration"),
                ["Kafka:BootstrapServers"] = bootstrapServers,
            })
            .Build();

    private static ServiceProvider Build(IConfiguration configuration) =>
        new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddRawSink(configuration)
            .BuildServiceProvider();
}

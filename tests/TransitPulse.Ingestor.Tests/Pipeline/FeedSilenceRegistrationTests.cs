using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class FeedSilenceRegistrationTests
{
    [Fact]
    public void Host_configure_registers_the_feed_silence_health_check_and_publisher()
    {
        // Same entry as Program: dropping AddFeedSilenceMonitoring from IngestorHost.Configure fails this test.
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RawSinkRegistration.ModeKey] = "archive",
            ["Archive:RootDirectory"] = Path.Combine(Path.GetTempPath(), "tp-host-registration"),
        });

        Assert.Equal(IngestorMode.Archive, IngestorHost.Configure(builder));
        using var host = builder.Build();

        var registration = Assert.Single(
            host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            entry => entry.Name == FeedSilenceHealthCheck.Name);
        Assert.IsType<FeedSilenceHealthCheck>(registration.Factory(host.Services));
        Assert.IsType<FeedSilenceLog>(host.Services.GetRequiredService<IHealthCheckPublisher>());
    }
}

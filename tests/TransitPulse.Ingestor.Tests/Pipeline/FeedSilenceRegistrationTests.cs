using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class FeedSilenceRegistrationTests
{
    [Fact]
    public void Host_registers_the_feed_silence_health_check_and_publisher()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new FeedMetrics(TestMeters.Create(), TimeProvider.System))
            .AddSingleton<IEnumerable<IFeedSource>>([])
            .AddFeedSilenceMonitoring()
            .BuildServiceProvider();

        var registration = Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            entry => entry.Name == FeedSilenceHealthCheck.Name);
        Assert.IsType<FeedSilenceHealthCheck>(registration.Factory(provider));
        Assert.IsType<FeedSilenceLog>(provider.GetRequiredService<IHealthCheckPublisher>());
    }
}

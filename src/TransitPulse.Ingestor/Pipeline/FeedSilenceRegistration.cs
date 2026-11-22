using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>Registers the in-process feed-silence health check and its log publisher.</summary>
internal static class FeedSilenceRegistration
{
    /// <summary>
    /// Adds <see cref="FeedSilenceHealthCheck"/> and <see cref="FeedSilenceLog"/> so silence episodes are observed
    /// without an HTTP endpoint (the collector has no web server).
    /// </summary>
    public static IServiceCollection AddFeedSilenceMonitoring(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<FeedSilenceHealthCheck>(FeedSilenceHealthCheck.Name);
        services.AddSingleton<IHealthCheckPublisher, FeedSilenceLog>();
        return services;
    }
}

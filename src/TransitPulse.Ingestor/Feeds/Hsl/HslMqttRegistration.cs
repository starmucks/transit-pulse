using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>Registers one <see cref="HslMqttSource"/> per feed listed in configuration section <c>Feeds:Mqtt</c>.</summary>
internal static class HslMqttRegistration
{
    /// <summary>Configuration section holding the list of MQTT subscriptions.</summary>
    public const string SectionName = "Feeds:Mqtt";

    /// <summary>Reads and checks the subscription list, then registers a source for each one.</summary>
    /// <param name="services">Service collection to add the sources to.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">A subscription has no topics, both topic forms, or invalid timeouts.</exception>
    public static IServiceCollection AddHslMqttFeeds(this IServiceCollection services, IConfiguration configuration)
    {
        var feeds = configuration.GetSection(SectionName).Get<List<HslMqttOptions>>() ?? [];
        foreach (var feed in feeds)
        {
            // Checked here, before the host starts, like the HTTP feeds; feed ids are checked by the pipeline.
            if ((feed.Topics.Length > 0) == (feed.Connections.Length > 0))
            {
                throw new InvalidOperationException($"MQTT feed '{feed.FeedId}': set either Topics (one connection) or Connections, not both or neither.");
            }

            if (feed.TopicGroups.Any(topics => topics.Length == 0 || topics.Any(string.IsNullOrWhiteSpace)))
            {
                throw new InvalidOperationException($"MQTT feed '{feed.FeedId}': Topics must list at least one non-empty topic filter.");
            }

            if (feed.SilenceTimeout <= TimeSpan.Zero || feed.ConnectTimeout <= TimeSpan.Zero)
            {
                throw new InvalidOperationException($"MQTT feed '{feed.FeedId}': SilenceTimeout and ConnectTimeout must be positive.");
            }

            // A limit within the reconnect timeout would report every reconnect as a silent feed.
            if (feed.MaxSilence <= feed.SilenceTimeout)
            {
                throw new InvalidOperationException($"MQTT feed '{feed.FeedId}': MaxSilence must be longer than SilenceTimeout.");
            }

            services.AddSingleton<IFeedSource>(provider => new HslMqttSource(
                feed,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<HslMqttSource>>()));
        }

        return services;
    }
}

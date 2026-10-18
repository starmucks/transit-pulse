using System.Net;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Http;

/// <summary>Registers one <see cref="HttpPollingSource"/> per feed listed in configuration section <c>Feeds:Http</c>.</summary>
internal static class HttpFeedRegistration
{
    /// <summary>Configuration section holding the list of polled feeds.</summary>
    public const string SectionName = "Feeds:Http";

    /// <summary>Reads and checks the feed list, then registers a source for each feed.</summary>
    /// <param name="services">Service collection to add the sources to.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">A feed has an invalid URL, interval or timeout.</exception>
    public static IServiceCollection AddHttpFeeds(this IServiceCollection services, IConfiguration configuration)
    {
        var feeds = configuration.GetSection(SectionName).Get<List<HttpFeedOptions>>() ?? [];
        foreach (var feed in feeds)
        {
            // Checked here, before the host starts: a typo in one feed must not surface hours later on the server.
            var url = feed.Url.Replace(HttpPollingSource.RequestorIdPlaceholder, "id", StringComparison.Ordinal);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"Feed '{feed.FeedId}': Url '{feed.Url}' is not an absolute HTTP(S) URL.");
            }

            if (feed.Interval <= TimeSpan.Zero || feed.Timeout <= TimeSpan.Zero)
            {
                throw new InvalidOperationException($"Feed '{feed.FeedId}': Interval and Timeout must be positive.");
            }

            services.AddSingleton<IFeedSource>(provider => new HttpPollingSource(
                feed,
                CreateHandler(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<HttpPollingSource>>()));
        }

        return services;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        // Some providers require gzip, and it shrinks large XML several times; the archive keeps the decoded body.
        AutomaticDecompression = DecompressionMethods.All,

        // A client that lives for the whole process would otherwise keep using the address DNS gave at startup.
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };
}

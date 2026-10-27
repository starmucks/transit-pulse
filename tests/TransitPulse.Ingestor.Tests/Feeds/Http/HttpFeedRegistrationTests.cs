using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TransitPulse.Ingestor.Feeds.Http;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Feeds.Http;

public sealed class HttpFeedRegistrationTests
{
    [Fact]
    public void Shipped_configuration_registers_every_feed()
    {
        // The Ingestor's own appsettings.json, copied next to the tests: a typo there must fail CI, not the server.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var sources = Resolve(configuration);
        var feedIds = sources.Select(source => source.FeedId).ToList();

        Assert.Equal(
            ["entur-sky-vm", "entur-sky-et", "entur-sky-sx", "mbta-vp", "mbta-tu", "mbta-alerts", "hsl-tu", "hsl-alerts", "weather"],
            feedIds);
        Assert.All(feedIds, id => Assert.True(FeedIds.IsValid(id), id));
        // Positions and predictions are checked for silence; alerts and weather may stay unchanged for long.
        Assert.Equal(
            [3, 5, null, 3, 5, null, 5, null, null],
            sources.Select(source => source.MaxSilence?.TotalMinutes));
    }

    [Theory]
    [InlineData("example.test/feed", "00:01:00")]
    [InlineData("ftp://example.test/feed", "00:01:00")]
    [InlineData("https://example.test/feed", "00:00:00")]
    [InlineData("https://example.test/feed", "00:01:00", "00:01:00")]
    public void Invalid_feed_fails_registration(string url, string interval, string? maxSilence = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feeds:Http:0:FeedId"] = "broken",
                ["Feeds:Http:0:Url"] = url,
                ["Feeds:Http:0:Interval"] = interval,
                ["Feeds:Http:0:MaxSilence"] = maxSilence,
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHttpFeeds(configuration));

        Assert.Contains("'broken'", error.Message, StringComparison.Ordinal);
    }

    private static List<IFeedSource> Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddHttpFeeds(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<IFeedSource>().ToList();
    }
}

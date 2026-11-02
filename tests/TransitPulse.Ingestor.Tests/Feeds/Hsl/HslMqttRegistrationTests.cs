using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Feeds.Hsl;

public sealed class HslMqttRegistrationTests
{
    [Fact]
    public void Shipped_configuration_registers_positions_and_stop_events()
    {
        // The Ingestor's own appsettings.json, copied next to the tests: a typo there must fail CI, not the server.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var sources = Resolve(configuration);

        Assert.Equal(["hsl", "hsl-stop"], sources.Select(source => source.FeedId));
        Assert.Equal([2, null], sources.Select(source => source.MaxSilence?.TotalMinutes));
        Assert.All(sources, source => Assert.True(FeedIds.IsValid(source.FeedId), source.FeedId));

        // Exactly the configured filters: a default in the options class would be kept and extended by the binder.
        var feeds = configuration.GetSection(HslMqttRegistration.SectionName).Get<List<HslMqttOptions>>()!;
        Assert.Equal(["/hfp/v2/journey/ongoing/vp/#"], feeds[0].Topics);
        Assert.Equal(["arr", "ars", "pde", "dep", "pas", "wait"], feeds[1].Topics.Select(topic => topic.Split('/')[5]));
    }

    [Theory]
    [InlineData("Topics:0", "", "Topics")]
    [InlineData("SilenceTimeout", "00:00:00", "SilenceTimeout")]
    [InlineData("MaxSilence", "00:01:00", "MaxSilence")]
    public void Invalid_subscription_fails_before_the_host_starts(string key, string value, string expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Feeds:Mqtt:0:FeedId"] = "hsl",
            ["Feeds:Mqtt:0:Topics:0"] = "/hfp/v2/journey/ongoing/vp/#",
            ["Feeds:Mqtt:0:SilenceTimeout"] = "00:01:00",
        };
        settings[$"Feeds:Mqtt:0:{key}"] = value;

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddHslMqttFeeds(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));

        Assert.Contains("'hsl'", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_section_registers_no_source()
    {
        Assert.Empty(Resolve(new ConfigurationBuilder().Build()));
    }

    private static List<IFeedSource> Resolve(IConfiguration configuration)
    {
        using var provider = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddHslMqttFeeds(configuration)
            .BuildServiceProvider();
        return [.. provider.GetServices<IFeedSource>()];
    }
}

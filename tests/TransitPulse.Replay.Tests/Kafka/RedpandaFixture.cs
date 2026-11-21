using Testcontainers.Redpanda;

namespace TransitPulse.Replay.Tests.Kafka;

/// <summary>One Redpanda broker in Docker per test class; tests isolate themselves by topic name.</summary>
public sealed class RedpandaFixture : IAsyncLifetime
{
    private readonly RedpandaContainer _container = new RedpandaBuilder("redpandadata/redpanda:v26.2.4").Build();

    /// <summary><c>host:port</c> of the broker's Kafka listener.</summary>
    public string BootstrapServers { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var address = _container.GetBootstrapAddress();
        BootstrapServers = address.Contains("://", StringComparison.Ordinal) ? new Uri(address).Authority : address;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

using Testcontainers.Redis;

namespace TransitPulse.Integration.Tests.Redis;

/// <summary>One Redis server in Docker per test class, with AOF so <c>WAITAOF</c> matches production checkpoints.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8.8.3-alpine")
        // Replace the image command entirely: flags alone would not start redis-server, and WAITAOF needs AOF.
        .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "everysec")
        .Build();

    /// <summary>Connection string for <see cref="StackExchange.Redis.ConnectionMultiplexer"/>.</summary>
    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

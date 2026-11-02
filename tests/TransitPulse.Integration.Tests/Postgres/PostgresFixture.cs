using Npgsql;
using Testcontainers.PostgreSql;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>One Postgres server in Docker per test class; each test takes its own empty database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The image the AppHost runs (TimescaleDB + PostGIS), so migrations meet the server the system uses.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17.11-ts2.30.2").Build();

    private readonly List<NpgsqlDataSource> _dataSources = [];
    private int _databases;

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    /// <summary>Creates an empty database and returns a data source for it.</summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _databases)}";
        await using (var server = NpgsqlDataSource.Create(_container.GetConnectionString()))
        {
            await using var create = server.CreateCommand($"CREATE DATABASE {name}");
            await create.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString);
        lock (_dataSources)
        {
            _dataSources.Add(dataSource);
        }

        return dataSource;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in _dataSources)
        {
            await dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

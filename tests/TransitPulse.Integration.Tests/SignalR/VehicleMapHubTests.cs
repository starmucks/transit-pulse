using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using TransitPulse.Api.Map;
using TransitPulse.Domain;
using TransitPulse.Infrastructure.Redis;
using TransitPulse.Integration.Tests.Redis;

namespace TransitPulse.Integration.Tests.SignalR;

/// <summary>Stage 3.5: two SignalR clients with different bboxes receive different vehicle sets.</summary>
public sealed class VehicleMapHubTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConnectionMultiplexer? _connection;
    private WebApplication? _app;
    private FakeTimeProvider? _clock;

    public async ValueTask InitializeAsync()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IConnectionMultiplexer>(_connection);
        builder.Services.AddSingleton<IVehicleViewportReader, VehicleViewportReader>();
        builder.Services.AddSingleton<MapSubscriptionRegistry>();
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<MapDeltaMetrics>();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSignalR();
        builder.Services.AddHostedService<MapDeltaTicker>();

        _app = builder.Build();
        _app.MapHub<VehicleMapHub>("/hubs/vehicles");
        await _app.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _connection?.Dispose();
    }

    [Fact]
    public async Task Two_clients_with_different_bboxes_receive_different_vehicles()
    {
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var writer = new VehicleGeoStore(_connection!, TimeSpan.FromSeconds(120));
        await writer.UpsertManyAsync(
            [
                Enriched("west-bus", 24.91, 60.17, "1009", at),
                Enriched("east-bus", 25.05, 60.17, "2015", at),
            ],
            Ct);

        var server = _app!.GetTestServer();
        await using var left = CreateClient(server);
        await using var right = CreateClient(server);
        await left.StartAsync(Ct);
        await right.StartAsync(Ct);

        var leftDeltas = new List<MapVehicleDelta>();
        var rightDeltas = new List<MapVehicleDelta>();
        left.On<MapVehicleDelta>(MapDeltaTicker.ClientMethod, delta => leftDeltas.Add(delta));
        right.On<MapVehicleDelta>(MapDeltaTicker.ClientMethod, delta => rightDeltas.Add(delta));

        await left.InvokeAsync("Subscribe", "hsl", 24.88, 60.15, 24.96, 60.20, (string?)null, Ct);
        await right.InvokeAsync("Subscribe", "hsl", 25.00, 60.15, 25.10, 60.20, (string?)null, Ct);

        // Advance the ticker clock and wait for both clients to get a non-empty added set.
        for (var i = 0; i < 20 && (leftDeltas.Count == 0 || rightDeltas.Count == 0); i++)
        {
            _clock!.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100, Ct);
        }

        Assert.NotEmpty(leftDeltas);
        Assert.NotEmpty(rightDeltas);
        Assert.Contains(leftDeltas.SelectMany(d => d.Added), v => v.VehicleId == "west-bus");
        Assert.DoesNotContain(leftDeltas.SelectMany(d => d.Added), v => v.VehicleId == "east-bus");
        Assert.Contains(rightDeltas.SelectMany(d => d.Added), v => v.VehicleId == "east-bus");
        Assert.DoesNotContain(rightDeltas.SelectMany(d => d.Added), v => v.VehicleId == "west-bus");
    }

    private static HubConnection CreateClient(TestServer server) =>
        new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/vehicles",
                options => options.HttpMessageHandlerFactory = _ => server.CreateHandler())
            .Build();

    private static EnrichedPosition Enriched(
        string vehicleId, double lon, double lat, string routeId, DateTimeOffset at) =>
        new(
            new VehiclePosition("hsl", vehicleId, $"{routeId}_trip", routeId, lat, lon, null, null, null, null, at),
            null,
            null);
}

using StackExchange.Redis;
using TransitPulse.Api;
using TransitPulse.Api.Map;
using TransitPulse.Infrastructure.Gtfs;
using TransitPulse.Infrastructure.Redis;
using TransitPulse.Infrastructure.StopEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// Connection string ConnectionStrings:transitpulse from the AppHost; adds Npgsql health check, traces and metrics.
builder.AddNpgsqlDataSource("transitpulse");
// Registers IDistributedCache on the AppHost Redis resource; HybridCache uses it as L2 automatically.
builder.AddRedisDistributedCache("redis");
// Shared multiplexer for GEO viewport reads and the SignalR backplane (same Redis resource).
builder.AddRedisClient("redis");
builder.Services.AddHybridCache();
builder.Services.AddOpenApi();
// Aspire client integrations register checks; this ensures MapHealthChecks has a builder even without them.
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IGtfsCatalogueReader, GtfsCatalogueReader>();
builder.Services.AddSingleton<GtfsCatalogueCache>();
builder.Services.AddSingleton<IStopEventReader, StopEventReader>();
builder.Services.AddSingleton<IPunctualityReader, PunctualityReader>();
builder.Services.AddSingleton<IVehicleViewportReader, VehicleViewportReader>();
builder.Services.AddSingleton<MapSubscriptionRegistry>();
builder.Services.AddSingleton<MapDeltaMetrics>();
builder.Services.AddHostedService<MapDeltaTicker>();

var redisConnection = builder.Configuration.GetConnectionString("redis")
    ?? throw new InvalidOperationException("Connection string 'redis' is required for SignalR backplane.");
builder.Services
    .AddSignalR()
    .AddStackExchangeRedis(redisConnection, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("transitpulse:signalr:");
    });

var app = builder.Build();

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapCatalogueEndpoints();
app.MapRealtimeEndpoints();
app.MapHub<VehicleMapHub>("/hubs/vehicles");

await app.RunAsync();

// Exposes the entry assembly to WebApplicationFactory tests without InternalsVisibleTo on the host.
/// <inheritdoc/>
public partial class Program;

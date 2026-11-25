using TransitPulse.Api;
using TransitPulse.Infrastructure.Gtfs;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// Connection string ConnectionStrings:transitpulse from the AppHost; adds Npgsql health check, traces and metrics.
builder.AddNpgsqlDataSource("transitpulse");
// Registers IDistributedCache on the AppHost Redis resource; HybridCache uses it as L2 automatically.
builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();
builder.Services.AddOpenApi();
// Aspire client integrations register checks; this ensures MapHealthChecks has a builder even without them.
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<GtfsCatalogueReader>();
builder.Services.AddSingleton<GtfsCatalogueCache>();

var app = builder.Build();

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapCatalogueEndpoints();

await app.RunAsync();

// Exposes the entry assembly to WebApplicationFactory tests without InternalsVisibleTo on the host.
/// <inheritdoc/>
public partial class Program;

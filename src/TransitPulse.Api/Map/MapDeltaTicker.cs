using Microsoft.AspNetCore.SignalR;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Map;

/// <summary>Pushes ~1 Hz viewport deltas to each subscribed SignalR connection (ADR-0017).</summary>
public sealed partial class MapDeltaTicker(
    MapSubscriptionRegistry registry,
    IVehicleViewportReader viewport,
    IHubContext<VehicleMapHub> hub,
    MapDeltaMetrics metrics,
    TimeProvider time,
    ILogger<MapDeltaTicker> logger) : BackgroundService
{
    /// <summary>SignalR client method name for a delta frame.</summary>
    public const string ClientMethod = "vehicleDelta";

    private static readonly TimeSpan _period = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_period, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var (subscription, lastSent, generation) in registry.Snapshot())
            {
                try
                {
                    await TickOneAsync(subscription, lastSent, generation, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    metrics.RedisError();
                    LogTickFailed(logger, ex, subscription.ConnectionId);
                }
            }
        }
    }

    private async Task TickOneAsync(
        MapSubscription subscription,
        IReadOnlyDictionary<string, MapVehicle> lastSent,
        long generation,
        CancellationToken cancellationToken)
    {
        var current = await viewport.ListInBoxAsync(
            subscription.FeedId,
            subscription.Box,
            subscription.RouteId,
            MapViewportLimits.MaxVehiclesPerFrame,
            cancellationToken);

        if (current.Count >= MapViewportLimits.MaxVehiclesPerFrame)
        {
            metrics.Truncated();
        }

        var delta = MapDeltaCalculator.Diff(lastSent, current);
        if (delta.Added.Count == 0 && delta.Updated.Count == 0 && delta.Removed.Count == 0)
        {
            // Still CAS last-sent so a mid-tick Subscribe is not overwritten with the old viewport.
            registry.ReplaceLastSent(subscription.ConnectionId, generation, current);
            return;
        }

        // CAS first: a concurrent Subscribe must not be rolled back by writing a stale Entry.
        if (!registry.ReplaceLastSent(subscription.ConnectionId, generation, current))
        {
            return;
        }

        // Subscribe may have landed after CAS; do not push a delta for the superseded viewport.
        if (!registry.IsCurrent(subscription.ConnectionId, generation))
        {
            return;
        }

        await hub.Clients.Client(subscription.ConnectionId).SendAsync(ClientMethod, delta, cancellationToken);
        metrics.DeltaPublished(delta.Added.Count, delta.Updated.Count, delta.Removed.Count);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Map tick failed for connection {ConnectionId}")]
    private static partial void LogTickFailed(ILogger logger, Exception exception, string connectionId);
}

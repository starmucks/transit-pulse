using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Kafka;

namespace TransitPulse.Ingestor.Tests.Kafka;

/// <summary>A real producer pointed at a closed port: everything up to delivery runs, delivery never succeeds.</summary>
public sealed class KafkaSinkTests
{
    private static readonly VehiclePosition[] _positions =
    [
        new("hsl", "40/415", null, null, 60.2, 24.9, null, null, null, null, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)),
    ];

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KafkaSink Create(Action<ProducerConfig>? configure = null)
    {
        var config = new ProducerConfig { BootstrapServers = "127.0.0.1:1", MessageTimeoutMs = 500 };
        configure?.Invoke(config);
        return new KafkaSink(config, "positions.raw", _time, NullLogger<KafkaSink>.Instance);
    }

    [Fact]
    public void Publish_only_enqueues_and_does_not_wait_for_the_broker()
    {
        using var sink = Create();

        Assert.True(sink.PublishAsync(_positions, Ct).AsTask().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Full_local_buffer_makes_publish_wait_until_cancelled()
    {
        using var sink = Create(config => config.QueueBufferingMaxMessages = 1);
        using var drainStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await sink.PublishAsync(_positions, Ct);
        var waiting = sink.PublishAsync(_positions, drainStop.Token).AsTask();

        // The back-off runs on the fake clock, so only cancellation can end it.
        Assert.False(waiting.IsCompleted);
        await drainStop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Undelivered_positions_are_counted_and_shutdown_still_completes()
    {
        using var sink = Create();

        await sink.PublishAsync([.. _positions, .. _positions], Ct);
        await sink.CompleteAsync(Ct);

        Assert.Equal(2, sink.FailedDeliveries);
    }
}

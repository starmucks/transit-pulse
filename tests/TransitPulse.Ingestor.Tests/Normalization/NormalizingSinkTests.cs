using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Pipeline;

namespace TransitPulse.Ingestor.Tests.Normalization;

public sealed class NormalizingSinkTests
{
    private readonly RecordingSink _archive = new();
    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NormalizingSink Create(
        RecordingPositionSink positions, IRawSink? archive = null, int channelCapacity = 100, ILogger<NormalizingSink>? logger = null) =>
        new(archive ?? _archive, new PositionRouter([new FakeNormalizer("hsl", "hsl")], NullLogger<PositionRouter>.Instance),
            positions, _time, logger ?? NullLogger<NormalizingSink>.Instance, channelCapacity);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WriteAsync(NormalizingSink sink, params byte[] seconds)
    {
        foreach (var s in seconds)
        {
            await sink.WriteAsync(FakeNormalizer.Record("hsl", s), Ct);
        }
    }

    [Fact]
    public async Task Every_record_is_archived_and_published()
    {
        var positions = new RecordingPositionSink();
        using var sink = Create(positions);

        await WriteAsync(sink, 1, 2, 3);
        await sink.CompleteAsync(Ct);

        Assert.Equal(3, _archive.Records.Count);
        Assert.Equal([1, 2, 3], positions.Positions.Select(p => (int)(p.ObservedAt - FakeNormalizer.T0).TotalSeconds));
        Assert.True(positions.Completed);
    }

    [Fact]
    public async Task Full_channel_drops_publishing_but_never_the_archive()
    {
        var gate = new TaskCompletionSource();
        var positions = new RecordingPositionSink { Gate = gate.Task };
        using var sink = Create(positions, channelCapacity: 2);

        await WriteAsync(sink, 1);
        // The loop holds record 1 in a blocked publish: the channel then fits records 2 and 3 only.
        await positions.PublishStarted.WaitAsync(Ct);
        await WriteAsync(sink, 2, 3, 4, 5, 6);
        gate.SetResult();
        await sink.CompleteAsync(Ct);

        Assert.Equal(6, _archive.Records.Count);
        Assert.Equal(3, positions.Positions.Count);
    }

    [Fact]
    public async Task Dropping_is_logged_once_per_episode()
    {
        var gate = new TaskCompletionSource();
        var positions = new RecordingPositionSink { Gate = gate.Task };
        var logger = new FakeLogger<NormalizingSink>();
        using var sink = Create(positions, channelCapacity: 1, logger: logger);

        await WriteAsync(sink, 1);
        await positions.PublishStarted.WaitAsync(Ct);
        await WriteAsync(sink, 2);
        // Ten drops in one episode: one warning.
        for (byte s = 3; s < 13; s++)
        {
            await sink.WriteAsync(new RawRecord("hsl", FakeNormalizer.T0.AddSeconds(s), [s], null), Ct);
        }

        gate.SetResult();
        await WaitForAsync(() => positions.Positions.Count == 2);
        // Accepted, but still inside the quiet period: the episode goes on.
        await sink.WriteAsync(new("hsl", FakeNormalizer.T0.AddSeconds(30), [30], null), Ct);
        await WaitForAsync(() => positions.Positions.Count == 3);
        await sink.WriteAsync(new("hsl", FakeNormalizer.T0.AddSeconds(90), [90], null), Ct);
        await sink.CompleteAsync(Ct);

        var logs = logger.Collector.GetSnapshot().Where(log => log.Message.Contains("channel", StringComparison.Ordinal)).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Information], logs.Select(log => log.Level));
        Assert.Contains("10 records", logs[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_during_a_blocked_publish_ends_the_loop()
    {
        // The host gave up waiting and disposes the container while a publish still hangs.
        var positions = new RecordingPositionSink { Gate = new TaskCompletionSource().Task };
        var sink = Create(positions);
        await WriteAsync(sink, 1);
        await positions.PublishStarted.WaitAsync(Ct);

        sink.Dispose();
        await sink.CompleteAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(positions.Completed);
    }

    [Fact]
    public async Task Archive_failure_propagates_and_nothing_is_published()
    {
        var positions = new RecordingPositionSink();
        using var sink = Create(positions, new RecordingSink { WriteFailure = new IOException("Disk full.") });

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(sink, 1));
        await sink.CompleteAsync(Ct);

        Assert.Empty(positions.Positions);
    }

    [Fact]
    public async Task Failed_publish_does_not_stop_the_loop()
    {
        var positions = new RecordingPositionSink { FailingCalls = new HashSet<int> { 1 } };
        using var sink = Create(positions);

        await WriteAsync(sink, 1, 2);
        await sink.CompleteAsync(Ct);

        Assert.Equal(2, Assert.Single(positions.Positions).ObservedAt.Second);
    }

    [Fact]
    public async Task Archive_is_closed_before_positions_are_flushed()
    {
        var archiveClosedFirst = false;
        var positions = new RecordingPositionSink { OnComplete = () => archiveClosedFirst = _archive.Completions == 1 };
        using var sink = Create(positions);

        await WriteAsync(sink, 1);
        await sink.CompleteAsync(Ct);

        Assert.True(archiveClosedFirst);
    }

    [Fact]
    public async Task Shutdown_stops_publishing_when_the_drain_budget_is_used_up()
    {
        // Never released: only the drain budget can end the blocked publish.
        var positions = new RecordingPositionSink { Gate = new TaskCompletionSource().Task };
        using var sink = Create(positions);

        await WriteAsync(sink, 1);
        await positions.PublishStarted.WaitAsync(Ct);
        await WriteAsync(sink, 2, 3);

        var completing = sink.CompleteAsync(Ct).AsTask();
        Assert.False(completing.IsCompleted);
        _time.Advance(NormalizingSink.DrainTimeout);
        await completing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(positions.Positions);
        Assert.True(positions.Completed);
        Assert.Equal(1, _archive.Completions);
    }
}

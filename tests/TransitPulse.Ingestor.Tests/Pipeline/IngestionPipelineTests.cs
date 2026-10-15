using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

public sealed class IngestionPipelineTests
{
    // Every wait is bounded: a broken pipeline must fail the test, not hang the run.
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delivers_records_from_all_sources()
    {
        var sink = new RecordingSink();
        using var pipeline = Create([ScriptedSource.Writing("a", 50), ScriptedSource.Writing("b", 50)], sink);

        await pipeline.StartAsync(Ct);
        await WaitUntilAsync(() => sink.Records.Count == 100);
        await pipeline.StopAsync(Ct);

        Assert.Equal(50, sink.Records.Count(r => r.FeedId == "a"));
        Assert.Equal(50, sink.Records.Count(r => r.FeedId == "b"));
        Assert.Equal(1, sink.Completions);
    }

    [Fact]
    public async Task Drains_channel_on_shutdown()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink { Gate = gate.Task };
        using var pipeline = Create([ScriptedSource.Writing("a", 100, allWritten)], sink);

        await pipeline.StartAsync(Ct);
        await allWritten.Task.WaitAsync(_timeout, Ct);

        // Shutdown starts while all 100 records are still waiting in the channel.
        var stopping = pipeline.StopAsync(Ct);
        gate.SetResult();
        await stopping.WaitAsync(_timeout, Ct);

        Assert.Equal(100, sink.Records.Count);
        Assert.Equal(100, sink.RecordsAtCompletion);
        Assert.Equal(1, sink.Completions);
    }

    [Fact]
    public async Task Restarts_failed_source_after_delay()
    {
        var time = new FakeTimeProvider();
        var started = time.GetUtcNow();
        var restartedAt = DateTimeOffset.MinValue;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource("a", async (run, output, cancellationToken) =>
        {
            if (run == 1)
            {
                failed.SetResult();
                throw new InvalidOperationException("Feed broke.");
            }

            restartedAt = time.GetUtcNow();
            await output.WriteAsync(ScriptedSource.Record("a", run), cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });
        var sink = new RecordingSink();
        using var pipeline = Create([source], sink, time);

        await pipeline.StartAsync(Ct);
        await failed.Task.WaitAsync(_timeout, Ct);
        await Task.Delay(50, Ct);
        Assert.Equal(1, source.Runs);

        // Advance in small steps: the restart timer may not exist yet when the first step happens.
        await WaitUntilAsync(() =>
        {
            if (source.Runs >= 2)
            {
                return true;
            }

            time.Advance(TimeSpan.FromSeconds(1));
            return false;
        });
        await WaitUntilAsync(() => sink.Records.Count == 1);
        await pipeline.StopAsync(Ct);

        Assert.True(restartedAt - started >= IngestionPipeline.SourceRestartDelay);
    }

    [Fact]
    public async Task Sink_failure_stops_sources_and_faults_pipeline()
    {
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource("a", async (_, output, cancellationToken) =>
        {
            // Checked on exit, not with token.Register: the write's own cancellation callback runs first and can
            // end this method inline, disposing a registration before its callback gets a turn.
            try
            {
                for (var i = 0; ; i++)
                {
                    await output.WriteAsync(ScriptedSource.Record("a", i), cancellationToken);
                }
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    sourceCancelled.TrySetResult();
                }
            }
        });
        var sink = new RecordingSink { WriteFailure = new IOException("Disk full.") };
        using var pipeline = Create([source], sink);

        await pipeline.StartAsync(Ct);

        await Assert.ThrowsAsync<IOException>(() => pipeline.ExecuteTask!.WaitAsync(_timeout, Ct));
        Assert.True(sourceCancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task Host_exits_with_1_when_sink_fails()
    {
        var host = CreateHost(ScriptedSource.Writing("a", count: 1), new RecordingSink { WriteFailure = new IOException("Disk full.") });

        Assert.Equal(1, await IngestionPipeline.RunHostAsync(host).WaitAsync(_timeout, Ct));
    }

    [Fact]
    public async Task Host_exits_with_0_on_requested_shutdown()
    {
        // Not disposed here: RunHostAsync runs the host, and running disposes it.
        var host = CreateHost(ScriptedSource.Writing("a", count: 1), new RecordingSink());
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        Assert.Equal(0, await IngestionPipeline.RunHostAsync(host).WaitAsync(_timeout, Ct));
    }

    private static IHost CreateHost(IFeedSource source, RecordingSink sink)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton<IRawSink>(sink);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<IngestionPipeline>();
        return builder.Build();
    }

    private static IngestionPipeline Create(IFeedSource[] sources, RecordingSink sink, TimeProvider? time = null) =>
        new(sources, sink, time ?? TimeProvider.System, NullLogger<IngestionPipeline>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(_timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

using System.Threading.Channels;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Runs every feed source and moves their raw records through one bounded channel into the sink.
/// The bounded channel is the backpressure point: when the sink falls behind, sources wait instead of
/// growing memory. On shutdown the sources stop first and records already in the channel are still written.
/// </summary>
/// <param name="sources">All configured feeds.</param>
/// <param name="sink">Where records are stored.</param>
/// <param name="timeProvider">Clock for restart delays.</param>
/// <param name="logger">Pipeline log.</param>
public sealed partial class IngestionPipeline(
    IEnumerable<IFeedSource> sources,
    IRawSink sink,
    TimeProvider timeProvider,
    ILogger<IngestionPipeline> logger) : BackgroundService
{
    /// <summary>About 8 s of all feeds at ~1 200 messages/s, a few MB of memory.</summary>
    internal const int ChannelCapacity = 10_000;

    /// <summary>
    /// How long after a stop request records left in the channel are still written. Then the rest is dropped and the
    /// files are closed: on a starved CPU an unbounded drain outlived the service stop timeout, the process was killed
    /// and every feed lost its open hour for ~10 s of records (2026-10-21).
    /// </summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Runs the host until shutdown and turns the pipeline outcome into the process exit code.</summary>
    /// <param name="host">Built host with this pipeline registered as a hosted service.</param>
    /// <returns>1 when the pipeline failed (the host stops but would otherwise exit with 0), else 0.</returns>
    /// <remarks>
    /// A failed pipeline must look like a failure to the service manager, or systemd's
    /// <c>Restart=on-failure</c> treats it as a clean stop and collection silently ends.
    /// </remarks>
    internal static async Task<int> RunHostAsync(IHost host)
    {
        // Resolved before running: RunAsync disposes the host and its service provider when it returns.
        var pipeline = host.Services.GetServices<IHostedService>().OfType<IngestionPipeline>().Single();
        await host.RunAsync();
        return pipeline.ExecuteTask is { IsFaulted: true } ? 1 : 0;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var feeds = sources.ToList();

        // Checked before anything is opened: a bad id found at the first write would fail the sink
        // with every other feed's hour still open.
        var invalid = feeds.Where(source => !FeedIds.IsValid(source.FeedId)).Select(source => $"'{source.FeedId}'").ToList();
        if (invalid.Count > 0)
        {
            throw new InvalidOperationException($"Feed ids {string.Join(", ", invalid)} are invalid; use {FeedIds.Rule}.");
        }

        // Two sources with one id would interleave their records in the same hourly archive file.
        var duplicates = feeds.GroupBy(source => source.FeedId).Where(group => group.Count() > 1).Select(group => $"'{group.Key}'").ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Feed ids {string.Join(", ", duplicates)} are used by more than one source.");
        }

        if (feeds.Count == 0)
        {
            LogNoSources();
        }

        var channel = Channel.CreateBounded<RawRecord>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        // Linked so that a failing sink can stop the sources too; otherwise they would wait on a full channel forever.
        using var sourcesStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Armed by the stop request itself, so the budget also covers the time the sources take to stop.
        using var drainStop = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        await using var armDrainStop = stoppingToken.Register(() => drainStop.CancelAfter(DrainTimeout));

        var writing = WriteAllAsync(channel.Reader, sourcesStop, drainStop.Token);
        try
        {
            await Task.WhenAll(feeds.Select(source => RunSourceAsync(source, channel.Writer, sourcesStop.Token)));
        }
        finally
        {
            // Shutdown phases are logged so the time a stop takes can be attributed (it is bounded by the host timeout).
            LogSourcesStopped(channel.Reader.Count);
            channel.Writer.Complete();
            await writing;
        }
    }

    private async Task WriteAllAsync(ChannelReader<RawRecord> reader, CancellationTokenSource sourcesStop, CancellationToken drainStop)
    {
        var completing = false;
        try
        {
            // No cancellation of reads or writes on purpose: after shutdown starts, the channel is still drained and
            // the files closed. The drain budget is checked only between records, never inside a row group write.
            await foreach (var record in reader.ReadAllAsync(CancellationToken.None))
            {
                if (drainStop.IsCancellationRequested)
                {
                    LogDrainBudgetUsedUp(DrainTimeout, reader.Count + 1);
                    break;
                }

                await sink.WriteAsync(record, CancellationToken.None);
            }

            completing = true;
            LogChannelDrained();
            await sink.CompleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A sink failure (disk full, I/O error) is fatal: stop everything and let the service manager restart the process.
            LogSinkFailed(ex);
            await sourcesStop.CancelAsync();
            if (!completing)
            {
                await TryCompleteSinkAsync();
            }

            throw;
        }
    }

    /// <summary>After a failed write, closes what is still intact so other feeds keep their open hour.</summary>
    private async Task TryCompleteSinkAsync()
    {
        try
        {
            await sink.CompleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogSinkCompleteFailed(ex);
        }
    }

    private async Task RunSourceAsync(IFeedSource source, ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        // Growing delays: a broker down for an hour costs a few dozen attempts and log lines, not one every 5 s.
        var backoff = new RestartBackoff(Random.Shared);
        while (true)
        {
            var started = timeProvider.GetTimestamp();
            Exception? failure = null;
            try
            {
                await source.RunAsync(output, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var delay = backoff.Next(timeProvider.GetElapsedTime(started));
            if (failure is null)
            {
                LogSourceReturned(source.FeedId, backoff.Failures, delay);
            }
            else
            {
                LogSourceFailed(failure, source.FeedId, backoff.Failures, delay);
            }

            try
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Feed {FeedId} failed ({Failures} in a row); restarting in {Delay}")]
    private partial void LogSourceFailed(Exception exception, string feedId, int failures, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {FeedId} stopped without a shutdown request ({Failures} in a row); restarting in {Delay}")]
    private partial void LogSourceReturned(string feedId, int failures, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "All feeds stopped; {Pending} records left in the channel to write")]
    private partial void LogSourcesStopped(int pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown drain budget of {Budget} used up; {Dropped} records dropped to close the files in time")]
    private partial void LogDrainBudgetUsedUp(TimeSpan budget, int dropped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel drained; closing open archive files")]
    private partial void LogChannelDrained();

    [LoggerMessage(Level = LogLevel.Warning, Message = "No feeds configured; the pipeline has nothing to collect")]
    private partial void LogNoSources();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Raw sink failed; stopping all feeds")]
    private partial void LogSinkFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Raw sink failed to close its files after a write failure")]
    private partial void LogSinkCompleteFailed(Exception exception);
}

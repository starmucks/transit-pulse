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

    internal static readonly TimeSpan SourceRestartDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var feeds = sources.ToList();
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
        var writing = WriteAllAsync(channel.Reader, sourcesStop);
        try
        {
            await Task.WhenAll(feeds.Select(source => RunSourceAsync(source, channel.Writer, sourcesStop.Token)));
        }
        finally
        {
            channel.Writer.Complete();
            await writing;
        }
    }

    private async Task WriteAllAsync(ChannelReader<RawRecord> reader, CancellationTokenSource sourcesStop)
    {
        try
        {
            // No cancellation here on purpose: after shutdown starts, the channel is still drained and the files closed.
            await foreach (var record in reader.ReadAllAsync(CancellationToken.None))
            {
                await sink.WriteAsync(record, CancellationToken.None);
            }

            await sink.CompleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A sink failure (disk full, I/O error) is fatal: stop everything and let the service manager restart the process.
            LogSinkFailed(ex);
            await sourcesStop.CancelAsync();
            throw;
        }
    }

    private async Task RunSourceAsync(IFeedSource source, ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await source.RunAsync(output, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                LogSourceReturned(source.FeedId, SourceRestartDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogSourceFailed(ex, source.FeedId, SourceRestartDelay);
            }

            try
            {
                await Task.Delay(SourceRestartDelay, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Feed {FeedId} failed; restarting in {Delay}")]
    private partial void LogSourceFailed(Exception exception, string feedId, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {FeedId} stopped without a shutdown request; restarting in {Delay}")]
    private partial void LogSourceReturned(string feedId, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No feeds configured; the pipeline has nothing to collect")]
    private partial void LogNoSources();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Raw sink failed; stopping all feeds")]
    private partial void LogSinkFailed(Exception exception);
}

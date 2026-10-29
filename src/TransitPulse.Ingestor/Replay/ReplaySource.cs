using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Replay;

/// <summary>
/// Feeds archived records back into the pipeline at their recorded pace, sped up by <see cref="ReplayOptions.Speed"/>,
/// or as fast as the pipeline takes them. Records keep their archived receive time, so normalization keeps and drops
/// exactly what it did live. When the range is played the source stops the host instead of returning: the pipeline
/// restarts a source that returns, which would replay the range again.
/// </summary>
/// <param name="options">The range, feeds and pace.</param>
/// <param name="archive">The archive to read; the same root the collector writes.</param>
/// <param name="timeProvider">Clock for pacing.</param>
/// <param name="lifetime">Stopped when the range is played.</param>
/// <param name="logger">Replay log.</param>
internal sealed partial class ReplaySource(
    IOptions<ReplayOptions> options,
    IOptions<ArchiveOptions> archive,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<ReplaySource> logger) : IFeedSource
{
    /// <summary>
    /// Shortest wait worth a timer. Timers tick every few milliseconds, so waiting per record would slow x10 below x1;
    /// records due sooner go out at once and the pace stays right on average.
    /// </summary>
    internal static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(10);

    /// <inheritdoc />
    /// <remarks>Records keep the ids of the feeds they were archived from; this one names the source only.</remarks>
    public string FeedId => "replay";

    /// <inheritdoc />
    public TimeSpan? MaxSilence => null;

    /// <summary>
    /// True when the replay stopped on an error, such as an unreadable file. The host stops either way, so the
    /// process reads this to exit with a failure code.
    /// </summary>
    public bool Failed { get; private set; }

    /// <inheritdoc />
    public async Task RunAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        try
        {
            await ReplayAsync(output, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Thrown, the error would make the pipeline restart the source and hit the same file again and again.
            Failed = true;
            LogFailed(ex);
        }

        lifetime.StopApplication();

        // Returning would make the pipeline start the source again; the stop request cancels this wait instead.
        await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
    }

    private async Task ReplayAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        var replay = options.Value;
        LogStarting(replay.From, replay.To, replay.Feeds, replay.Speed);
        var started = timeProvider.GetTimestamp();
        DateTimeOffset? first = null;
        long count = 0;
        await foreach (var record in ArchiveReader.ReadAsync(archive.Value.RootDirectory, replay.Feeds, replay.From, replay.To, cancellationToken))
        {
            first ??= record.ReceivedAt;
            if (replay.Speed > 0)
            {
                // The schedule is anchored to the first record: a slow stretch is caught up, not carried forward.
                var ahead = (record.ReceivedAt - first.Value) / replay.Speed - timeProvider.GetElapsedTime(started);
                if (ahead >= MinWait)
                {
                    await Task.Delay(ahead, timeProvider, cancellationToken);
                }
            }

            await output.WriteAsync(record, cancellationToken);
            count++;
        }

        var elapsed = timeProvider.GetElapsedTime(started);
        if (count == 0)
        {
            LogNothingToReplay();
        }
        else
        {
            var rate = count / Math.Max(elapsed.TotalSeconds, 0.001);
            LogFinished(count, elapsed, rate);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replaying {From:O} to {To:O} of {Feeds} at speed {Speed} (0 = as fast as possible)")]
    private partial void LogStarting(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> feeds, double speed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay finished: {Records} records in {Elapsed}, {Rate:F0} records/s; stopping")]
    private partial void LogFinished(long records, TimeSpan elapsed, double rate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nothing to replay: the archive has no records of these feeds in the range; stopping")]
    private partial void LogNothingToReplay();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Replay failed; stopping")]
    private partial void LogFailed(Exception exception);
}

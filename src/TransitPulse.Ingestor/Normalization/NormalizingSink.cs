using System.Threading.Channels;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Normalization;

/// <summary>
/// Wraps the raw archive and branches every record off to normalization: the record is archived first, then
/// offered to a separate bounded channel that a background loop normalizes and publishes. When the channel is
/// full the record is dropped from publishing, never delayed: a slow or failed broker must not hold back the
/// archive, which is the source of truth and lets dropped positions be rebuilt by replay.
/// </summary>
internal sealed partial class NormalizingSink : IRawSink, IDisposable
{
    /// <summary>About 10 s of all feeds; covers a short broker hiccup without dropping.</summary>
    internal const int DefaultChannelCapacity = 10_000;

    /// <summary>How long after the archive is closed the remaining records are still published on shutdown.</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private const string ChannelKey = "channel";

    private readonly IRawSink _archive;
    private readonly PositionRouter _router;
    private readonly IPositionSink _positions;
    private readonly PositionMetrics _metrics;
    private readonly ILogger<NormalizingSink> _logger;
    private readonly Channel<RawRecord> _channel;
    private readonly CancellationTokenSource _drainStop;

    // Read once: the token stays usable after Dispose, the source's Token property would throw.
    private readonly CancellationToken _drainToken;
    private readonly Task _publishing;

    // One per side, each used by one thread only: drops by the writer, publish failures by the loop.
    private readonly FailureEpisodes _drops = new(FailureEpisodes.DefaultQuietPeriod);
    private readonly FailureEpisodes _publishFailures = new(FailureEpisodes.DefaultQuietPeriod);

    /// <summary>Creates the sink and starts its publishing loop.</summary>
    /// <param name="archive">The raw archive; every record goes there first.</param>
    /// <param name="router">Turns records into positions.</param>
    /// <param name="positions">Where positions are published.</param>
    /// <param name="timeProvider">Clock for the shutdown drain budget.</param>
    /// <param name="metrics">Count of records dropped on a full channel.</param>
    /// <param name="logger">Log for dropped records and publish failures.</param>
    /// <param name="channelCapacity">Records waiting to be published before new ones are dropped.</param>
    public NormalizingSink(
        IRawSink archive,
        PositionRouter router,
        IPositionSink positions,
        TimeProvider timeProvider,
        PositionMetrics metrics,
        ILogger<NormalizingSink> logger,
        int channelCapacity = DefaultChannelCapacity)
    {
        _archive = archive;
        _router = router;
        _positions = positions;
        _metrics = metrics;
        _logger = logger;
        _channel = Channel.CreateBounded<RawRecord>(new BoundedChannelOptions(channelCapacity)
        {
            // Wait, not DropWrite: with Wait a full channel makes TryWrite return false, so drops can be counted.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _drainStop = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        _drainToken = _drainStop.Token;
        _publishing = Task.Run(PublishAllAsync);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(RawRecord record, CancellationToken cancellationToken)
    {
        // An archive failure propagates: the pipeline treats it as fatal, as it does without this sink.
        await _archive.WriteAsync(record, cancellationToken);

        // Logged per episode, not per record: while the broker is down this happens ~1 000 times a second, and
        // near the limit full and free alternate many times a second.
        if (_channel.Writer.TryWrite(record))
        {
            if (_drops.AnyOpen && _drops.Succeed(ChannelKey, record.ReceivedAt) is var dropped and > 0)
            {
                LogDroppingEnded(dropped);
            }
        }
        else
        {
            _metrics.Skipped(record.FeedId, PositionMetrics.Backpressure);
            if (_drops.Fail(ChannelKey, record.ReceivedAt))
            {
                LogDroppingStarted(_channel.Reader.Count);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>The archive is closed first: it matters most and the host gives shutdown a limited time.</remarks>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _archive.CompleteAsync(cancellationToken);
        }
        finally
        {
            _channel.Writer.TryComplete();
            try
            {
                _drainStop.CancelAfter(DrainTimeout);
            }
            catch (ObjectDisposedException)
            {
                // Disposed first (the host gave up waiting): Dispose has already cancelled the loop.
            }

            await _publishing;
            await CompletePositionsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stops the publishing loop at once and frees its timer. Normally the container calls it after the pipeline has
    /// completed this sink; it also ends the loop when <see cref="CompleteAsync"/> never ran or the host stopped waiting.
    /// </summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _drainStop.Cancel();
        _drainStop.Dispose();
    }

    private async Task PublishAllAsync()
    {
        await foreach (var record in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (_drainToken.IsCancellationRequested)
            {
                LogDrainBudgetUsedUp(DrainTimeout, _channel.Reader.Count + 1);
                return;
            }

            try
            {
                var positions = _router.Route(record);
                if (positions.Count > 0)
                {
                    await _positions.PublishAsync(positions, _drainToken);
                }

                if (_publishFailures.AnyOpen && _publishFailures.Succeed(record.FeedId, record.ReceivedAt) is var failed and > 0)
                {
                    LogPublishRecovered(record.FeedId, failed);
                }
            }
            catch (OperationCanceledException) when (_drainToken.IsCancellationRequested)
            {
                LogDrainBudgetUsedUp(DrainTimeout, _channel.Reader.Count + 1);
                return;
            }
            catch (Exception ex)
            {
                // These positions can be rebuilt from the archive by replay; stopping the loop would lose all later ones.
                if (_publishFailures.Fail(record.FeedId, record.ReceivedAt))
                {
                    LogPublishFailing(ex, record.FeedId, record.ReceivedAt);
                }
            }
        }
    }

    // A failed flush loses only positions the archive still has; it must not turn a clean stop into a failure.
    private async Task CompletePositionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _positions.CompleteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            LogCompleteFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Position channel full ({Pending} waiting); records are archived but not published until it drains")]
    private partial void LogDroppingStarted(int pending);

    [LoggerMessage(Level = LogLevel.Information, Message = "Position channel drains again; {Dropped} records were archived but not published")]
    private partial void LogDroppingEnded(long dropped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing positions of feed {FeedId} fails (first received at {ReceivedAt}); they are dropped until it recovers")]
    private partial void LogPublishFailing(Exception exception, string feedId, DateTimeOffset receivedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Publishing positions of feed {FeedId} works again; {Failed} records were not published")]
    private partial void LogPublishRecovered(string feedId, long failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown drain budget of {Budget} used up; {Dropped} records not published")]
    private partial void LogDrainBudgetUsedUp(TimeSpan budget, int dropped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flushing positions on shutdown failed")]
    private partial void LogCompleteFailed(Exception exception);
}

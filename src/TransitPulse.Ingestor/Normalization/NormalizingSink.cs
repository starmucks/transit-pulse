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

    private readonly IRawSink _archive;
    private readonly PositionRouter _router;
    private readonly IPositionSink _positions;
    private readonly ILogger<NormalizingSink> _logger;
    private readonly Channel<RawRecord> _channel;
    private readonly CancellationTokenSource _drainStop;
    private readonly Task _publishing;

    // Touched only by the writer side (the pipeline's single reader).
    private long _dropped;

    /// <summary>Creates the sink and starts its publishing loop.</summary>
    /// <param name="archive">The raw archive; every record goes there first.</param>
    /// <param name="router">Turns records into positions.</param>
    /// <param name="positions">Where positions are published.</param>
    /// <param name="timeProvider">Clock for the shutdown drain budget.</param>
    /// <param name="logger">Log for dropped records and publish failures.</param>
    /// <param name="channelCapacity">Records waiting to be published before new ones are dropped.</param>
    public NormalizingSink(
        IRawSink archive,
        PositionRouter router,
        IPositionSink positions,
        TimeProvider timeProvider,
        ILogger<NormalizingSink> logger,
        int channelCapacity = DefaultChannelCapacity)
    {
        _archive = archive;
        _router = router;
        _positions = positions;
        _logger = logger;
        _channel = Channel.CreateBounded<RawRecord>(new BoundedChannelOptions(channelCapacity)
        {
            // Wait, not DropWrite: with Wait a full channel makes TryWrite return false, so drops can be counted.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _drainStop = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        _publishing = Task.Run(PublishAllAsync);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(RawRecord record, CancellationToken cancellationToken)
    {
        // An archive failure propagates: the pipeline treats it as fatal, as it does without this sink.
        await _archive.WriteAsync(record, cancellationToken);

        if (_channel.Writer.TryWrite(record))
        {
            if (_dropped > 0)
            {
                LogDroppingEnded(_dropped);
                _dropped = 0;
            }

            return;
        }

        // Logged once per episode, not per record: while the broker is down this happens ~1 000 times a second.
        if (_dropped++ == 0)
        {
            LogDroppingStarted(_channel.Reader.Count);
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
            _drainStop.CancelAfter(DrainTimeout);
            await _publishing;
            await CompletePositionsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Ends the publishing loop if <see cref="CompleteAsync"/> never ran (the host failed to start) and frees its timer.
    /// The container calls it when the host is disposed, after the pipeline has completed this sink.
    /// </summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _drainStop.Dispose();
    }

    private async Task PublishAllAsync()
    {
        await foreach (var record in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (_drainStop.IsCancellationRequested)
            {
                LogDrainBudgetUsedUp(DrainTimeout, _channel.Reader.Count + 1);
                return;
            }

            try
            {
                var positions = _router.Route(record);
                if (positions.Count > 0)
                {
                    await _positions.PublishAsync(positions, _drainStop.Token);
                }
            }
            catch (OperationCanceledException) when (_drainStop.IsCancellationRequested)
            {
                LogDrainBudgetUsedUp(DrainTimeout, _channel.Reader.Count + 1);
                return;
            }
            catch (Exception ex)
            {
                // These positions can be rebuilt from the archive by replay; stopping the loop would lose all later ones.
                LogPublishFailed(ex, record.FeedId, record.ReceivedAt);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing positions of feed {FeedId} received at {ReceivedAt} failed; dropped")]
    private partial void LogPublishFailed(Exception exception, string feedId, DateTimeOffset receivedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown drain budget of {Budget} used up; {Dropped} records not published")]
    private partial void LogDrainBudgetUsedUp(TimeSpan budget, int dropped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flushing positions on shutdown failed")]
    private partial void LogCompleteFailed(Exception exception);
}

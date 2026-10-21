using System.Collections.Concurrent;
using System.Threading.Channels;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Pipeline;

/// <summary>Feed whose behaviour per run is given by the test; the run number starts at 1.</summary>
internal sealed class ScriptedSource(string feedId, Func<int, ChannelWriter<RawRecord>, CancellationToken, Task> run) : IFeedSource
{
    private int _runs;

    public string FeedId => feedId;

    public int Runs => Volatile.Read(ref _runs);

    public Task RunAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken) =>
        run(Interlocked.Increment(ref _runs), output, cancellationToken);

    public static RawRecord Record(string feedId, int number) => new(feedId, DateTimeOffset.UnixEpoch, [(byte)number], null);

    /// <summary>A healthy feed: writes <paramref name="count"/> records, then waits for shutdown.</summary>
    public static ScriptedSource Writing(string feedId, int count, TaskCompletionSource? allWritten = null) =>
        new(feedId, async (_, output, cancellationToken) =>
        {
            for (var i = 0; i < count; i++)
            {
                await output.WriteAsync(Record(feedId, i), cancellationToken);
            }

            allWritten?.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });
}

/// <summary>Sink that keeps everything in memory; can be held on a gate or made to fail.</summary>
internal sealed class RecordingSink : IRawSink
{
    private readonly ConcurrentQueue<RawRecord> _records = new();
    private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completions;

    /// <summary>Every write waits for this task first.</summary>
    public Task? Gate { get; init; }

    /// <summary>Completes when the first write starts, before it waits on <see cref="Gate"/>.</summary>
    public Task WriteStarted => _writeStarted.Task;

    /// <summary>Every write throws this exception.</summary>
    public Exception? WriteFailure { get; init; }

    public IReadOnlyCollection<RawRecord> Records => _records;

    public int Completions => Volatile.Read(ref _completions);

    /// <summary>How many records had been written when <see cref="CompleteAsync"/> ran.</summary>
    public int RecordsAtCompletion { get; private set; } = -1;

    public async ValueTask WriteAsync(RawRecord record, CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        _writeStarted.TrySetResult();
        if (Gate is not null)
        {
            await Gate;
        }

        _records.Enqueue(record);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        RecordsAtCompletion = _records.Count;
        Interlocked.Increment(ref _completions);
        return ValueTask.CompletedTask;
    }
}

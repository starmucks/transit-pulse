using System.Collections.Concurrent;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Normalization;

/// <summary>
/// Returns one position of vehicle "v1" observed the payload's first byte of seconds after <see cref="T0"/>;
/// returns nothing for <see cref="SilentFeed"/> (reporting <see cref="SilentDropReason"/> when set) and throws for
/// <see cref="FailOnSeconds"/>.
/// </summary>
internal sealed class FakeNormalizer(string cityFeedId, params string[] rawFeedIds) : IPositionNormalizer
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public IReadOnlySet<string> RawFeedIds { get; } = rawFeedIds.ToHashSet();

    public string? SilentFeed { get; init; }

    public string? SilentDropReason { get; init; }

    public int? FailOnSeconds { get; init; }

    public int Calls { get; private set; }

    /// <summary>Raw record whose single payload byte is the number of seconds after <see cref="T0"/>.</summary>
    public static RawRecord Record(string feedId, byte seconds = 0) => new(feedId, T0, [seconds], null);

    public IReadOnlyList<VehiclePosition> Normalize(RawRecord record, IPositionDrops drops)
    {
        Calls++;
        var seconds = record.Payload[0];
        if (seconds == FailOnSeconds)
        {
            throw new FormatException("Broken payload.");
        }

        if (record.FeedId == SilentFeed)
        {
            if (SilentDropReason is not null)
            {
                drops.Drop(SilentDropReason);
            }

            return [];
        }

        return [new VehiclePosition(cityFeedId, "v1", null, null, 60.2, 24.9, null, null, null, null, T0.AddSeconds(seconds))];
    }
}

/// <summary>Keeps the drop reasons a normalizer reports, in order.</summary>
internal sealed class DropLog : IPositionDrops
{
    public List<string> Reasons { get; } = [];

    public void Drop(string reason) => Reasons.Add(reason);
}

/// <summary>Position sink that keeps everything in memory; can be held on a gate or made to fail.</summary>
internal sealed class RecordingPositionSink : IPositionSink
{
    private readonly ConcurrentQueue<VehiclePosition> _positions = new();
    private readonly TaskCompletionSource _publishStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    /// <summary>Every publish waits for this task first, giving up when its token is cancelled.</summary>
    public Task? Gate { get; init; }

    /// <summary>Completes when the first publish starts, before it waits on <see cref="Gate"/>.</summary>
    public Task PublishStarted => _publishStarted.Task;

    /// <summary>Publish call numbers (starting at 1) that throw.</summary>
    public IReadOnlySet<int> FailingCalls { get; init; } = new HashSet<int>();

    /// <summary>Runs inside <see cref="CompleteAsync"/>, to observe what has happened by then.</summary>
    public Action? OnComplete { get; init; }

    public IReadOnlyCollection<VehiclePosition> Positions => _positions;

    public bool Completed { get; private set; }

    public async ValueTask PublishAsync(IReadOnlyList<VehiclePosition> positions, CancellationToken cancellationToken)
    {
        _publishStarted.TrySetResult();
        if (FailingCalls.Contains(Interlocked.Increment(ref _calls)))
        {
            throw new InvalidOperationException("Broker unavailable.");
        }

        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        foreach (var position in positions)
        {
            _positions.Enqueue(position);
        }
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        OnComplete?.Invoke();
        Completed = true;
        return ValueTask.CompletedTask;
    }
}

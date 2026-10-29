using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Replay;

namespace TransitPulse.Ingestor.Tests.Replay;

public sealed class ReplaySourceTests : IDisposable
{
    private static readonly DateTimeOffset _day = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // Long enough for a source that should not move to show it would have.
    private static readonly TimeSpan _settle = TimeSpan.FromMilliseconds(100);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-replay-");
    private readonly FakeTimeProvider _time = new(_day.AddDays(8));
    private readonly FakeLifetime _lifetime = new();
    private readonly Channel<RawRecord> _output = Channel.CreateUnbounded<RawRecord>();
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly List<RawRecord> _received = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _stop.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public async Task Real_time_replay_keeps_the_recorded_gaps()
    {
        await WriteAsync("a", "00:00:00", "00:00:10", "00:00:20");

        var run = Start(speed: 1);

        await WaitForAsync(1);
        await AssertStillAsync(after: TimeSpan.FromSeconds(9.9), count: 1);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        await WaitForAsync(2);
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitForAsync(3);
        await StopAsync(run);
    }

    [Fact]
    public async Task Speed_ten_shortens_the_gaps_tenfold()
    {
        await WriteAsync("a", "00:00:00", "00:00:10", "00:00:20");

        var run = Start(speed: 10);

        await WaitForAsync(1);
        await AssertStillAsync(after: TimeSpan.FromSeconds(0.9), count: 1);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        await WaitForAsync(2);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(3);
        await StopAsync(run);
    }

    [Fact]
    public async Task Speed_zero_sends_everything_at_once_with_the_archived_receive_times()
    {
        await WriteAsync("a", "00:00:00", "00:30:00");
        await WriteAsync("b", "00:10:00");

        var run = Start(speed: 0, feeds: ["a", "b"]);

        await WaitForAsync(3);
        Assert.Equal([At("00:00:00"), At("00:10:00"), At("00:30:00")], _received.Select(record => record.ReceivedAt));
        Assert.Equal(["a", "b", "a"], _received.Select(record => record.FeedId));
        await StopAsync(run);
    }

    [Fact]
    public async Task Finished_replay_stops_the_host_and_waits_instead_of_returning()
    {
        await WriteAsync("a", "00:00:00");

        var run = Start(speed: 0);

        await WaitUntilAsync(() => _lifetime.StopRequested);
        await Task.Delay(_settle, Ct);

        // A returned source would be started again by the pipeline and replay the range once more.
        Assert.False(run.IsCompleted);
        await StopAsync(run);
    }

    [Fact]
    public async Task Empty_range_stops_the_host_without_records()
    {
        var run = Start(speed: 1);

        await WaitUntilAsync(() => _lifetime.StopRequested);
        Assert.False(_output.Reader.TryRead(out _));
        await StopAsync(run);
    }

    [Fact]
    public async Task Stop_request_ends_a_replay_in_the_middle()
    {
        await WriteAsync("a", "00:00:00", "00:50:00");

        var run = Start(speed: 1);

        await WaitForAsync(1);
        await StopAsync(run);
        Assert.False(_lifetime.StopRequested);
    }

    [Theory]
    [InlineData("01:00:00", "01:00:00", 1, "a", "Replay:To must be later")]
    [InlineData("01:00:00", "02:00:00", -1, "a", "Replay:Speed must be")]
    [InlineData("01:00:00", "02:00:00", 51, "a", "Replay:Speed must be")]
    [InlineData("01:00:00", "02:00:00", double.NaN, "a", "Replay:Speed must be")]
    [InlineData("01:00:00", "02:00:00", 1, "", "Replay:Feeds must name")]
    [InlineData("01:00:00", "02:00:00", 1, "a,HSL", "Replay:Feeds 'HSL' is invalid")]
    [InlineData("01:00:00", "02:00:00", 1, "a,b,a", "Replay:Feeds lists 'a' more than once")]
    public void Validator_rejects_unusable_settings(string from, string to, double speed, string feeds, string error)
    {
        var options = Options(At(from), At(to), speed, feeds.Split(',', StringSplitOptions.RemoveEmptyEntries));

        var result = new ReplayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith(error, StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_accepts_a_paced_replay()
    {
        var result = new ReplayOptionsValidator().Validate(null, Options(At("01:00:00"), At("02:00:00"), 10, ["hsl", "mbta-vp"]));

        Assert.True(result.Succeeded);
    }

    private static DateTimeOffset At(string time) => _day + TimeSpan.Parse(time, CultureInfo.InvariantCulture);

    private static ReplayOptions Options(DateTimeOffset from, DateTimeOffset to, double speed, string[] feeds) =>
        new() { From = from, To = to, Speed = speed, Feeds = [.. feeds] };

    private Task Start(double speed, string[]? feeds = null)
    {
        var source = new ReplaySource(
            Microsoft.Extensions.Options.Options.Create(Options(At("00:00:00"), At("01:00:00"), speed, feeds ?? ["a"])),
            Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { RootDirectory = _dir.FullName }),
            _time,
            _lifetime,
            NullLogger<ReplaySource>.Instance);
        return source.RunAsync(_output.Writer, _stop.Token);
    }

    // One file per feed and hour 00, like the collector writes it.
    private async Task WriteAsync(string feed, params string[] times)
    {
        var directory = Path.Combine(_dir.FullName, feed, "2026-10-21");
        Directory.CreateDirectory(directory);
        var file = await ArchiveFile.CreateAsync(Path.Combine(directory, $"{feed}-20261021T00Z-0.parquet"), rowGroupMaxBytes: 1_000_000, Ct);
        foreach (var time in times)
        {
            await file.AddAsync(new RawRecord(feed, At(time), [1], null), Ct);
        }

        await file.CompleteAsync(Ct);
    }

    private void Drain()
    {
        while (_output.Reader.TryRead(out var record))
        {
            _received.Add(record);
        }
    }

    private Task WaitForAsync(int count) => WaitUntilAsync(() =>
    {
        Drain();
        return _received.Count >= count;
    });

    // Moves the clock short of the next record and checks that it has not gone out.
    private async Task AssertStillAsync(TimeSpan after, int count)
    {
        _time.Advance(after);
        await Task.Delay(_settle, Ct);
        Drain();
        Assert.Equal(count, _received.Count);
    }

    private async Task StopAsync(Task run)
    {
        await _stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(_timeout, Ct));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(_timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private volatile bool _stopRequested;

        public bool StopRequested => _stopRequested;

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopRequested = true;
    }
}

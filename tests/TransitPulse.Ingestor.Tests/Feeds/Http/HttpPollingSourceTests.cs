using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TransitPulse.Ingestor.Feeds.Http;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Feeds.Http;

public sealed class HttpPollingSourceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Writes_response_body_with_configured_headers()
    {
        await using var run = PollingRun.Start([_ => Ok("a")], new HttpFeedOptions
        {
            FeedId = "test",
            Url = "https://example.test/feed",
            Headers = { ["ET-Client-Name"] = "test-client" },
        });

        // No time has passed yet: the first poll runs as soon as the source starts.
        var record = await run.Output.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);

        Assert.Equal("test", record.FeedId);
        Assert.Equal("a", Encoding.UTF8.GetString(record.Payload));
        Assert.Null(record.Topic);
        Assert.Equal(run.StartedAt, record.ReceivedAt);
        var request = run.Handler.Requests.Single();
        Assert.Equal("https://example.test/feed", request.RequestUri!.ToString());
        Assert.Equal("test-client", Assert.Single(request.Headers.GetValues("ET-Client-Name")));
    }

    [Fact]
    public async Task Sends_validators_and_skips_not_modified()
    {
        var lastModified = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        await using var run = PollingRun.Start([_ => Ok("a", "\"v1\"", lastModified), _ => NotModified()]);

        await run.PollsAsync(3);

        Assert.Single(run.Records());
        var second = run.Handler.Requests.ElementAt(1);
        Assert.Equal("\"v1\"", Assert.Single(second.Headers.IfNoneMatch).Tag);
        Assert.Equal(lastModified, second.Headers.IfModifiedSince);
    }

    [Fact]
    public async Task Skips_body_identical_to_previous()
    {
        await using var run = PollingRun.Start([_ => Ok("a"), _ => Ok("a"), _ => Ok("b"), _ => Ok("a")]);

        await run.PollsAsync(5);

        Assert.Equal(["a", "b", "a"], run.Records().Select(r => Encoding.UTF8.GetString(r.Payload)));
    }

    [Fact]
    public async Task Keeps_polling_after_failed_polls()
    {
        await using var run = PollingRun.Start(
            [
                _ => throw new HttpRequestException("connection refused"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => Ok("longer than the limit"),
                _ => Ok("ok"),
            ],
            new HttpFeedOptions { FeedId = "test", Url = "https://example.test/feed", MaxResponseBytes = 8 });

        await run.PollsAsync(5);

        Assert.Equal(["ok"], run.Records().Select(r => Encoding.UTF8.GetString(r.Payload)));
        Assert.False(run.Task.IsCompleted);
    }

    [Fact]
    public async Task Replaces_requestor_id_placeholder_with_one_id_per_run()
    {
        await using var run = PollingRun.Start(
            [_ => Ok("a")],
            new HttpFeedOptions { FeedId = "test", Url = "https://example.test/vm?requestorId={requestorId}" });

        await run.PollsAsync(2);

        var urls = run.Handler.Requests.Select(r => r.RequestUri!.ToString()).ToList();
        Assert.Matches(@"^https://example\.test/vm\?requestorId=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", urls[0]);
        Assert.Equal(urls[0], urls[1]);
    }

    [Fact]
    public async Task Renews_requestor_id_after_a_poll_whose_response_may_have_been_lost()
    {
        await using var run = PollingRun.Start(
            [
                _ => Ok("a", "\"v1\""),
                _ => throw new HttpRequestException("connection reset"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => Ok("b"),
            ],
            new HttpFeedOptions { FeedId = "test", Url = "https://example.test/vm?requestorId={requestorId}" });

        await run.PollsAsync(5);

        var urls = run.Handler.Requests.Select(r => r.RequestUri!.ToString()).ToList();
        Assert.Equal(urls[0], urls[1]);
        Assert.NotEqual(urls[1], urls[2]);
        Assert.NotEqual(urls[2], urls[3]);
        Assert.Equal(urls[3], urls[4]);

        // The first response's ETag goes with the old id only.
        Assert.Equal("\"v1\"", Assert.Single(run.Handler.Requests.ElementAt(1).Headers.IfNoneMatch).Tag);
        Assert.Empty(run.Handler.Requests.ElementAt(2).Headers.IfNoneMatch);
    }

    [Fact]
    public async Task Keeps_requestor_id_after_a_rejected_request()
    {
        await using var run = PollingRun.Start(
            [_ => Ok("a"), _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => Ok("b")],
            new HttpFeedOptions { FeedId = "test", Url = "https://example.test/vm?requestorId={requestorId}" });

        await run.PollsAsync(3);

        Assert.Single(run.Handler.Requests.Select(r => r.RequestUri!.ToString()).Distinct());
    }

    [Fact]
    public async Task Waits_as_long_as_retry_after_asks()
    {
        await using var run = PollingRun.Start([_ => Ok("a"), _ => TooManyRequests(TimeSpan.FromMinutes(2)), _ => Ok("b"), _ => Ok("c")]);

        await run.PollsAsync(4);

        var gaps = run.Gaps();
        AssertGap(TimeSpan.FromMinutes(2), gaps[1]);
        AssertGap(_interval, gaps[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task Follows_retry_after_only_on_429_and_503(HttpStatusCode status, bool followed)
    {
        await using var run = PollingRun.Start([_ => Rejected(status, TimeSpan.FromMinutes(2)), _ => Ok("a")]);

        await run.PollsAsync(2);

        AssertGap(followed ? TimeSpan.FromMinutes(2) : _interval, run.Gaps()[0]);
    }

    [Fact]
    public async Task Doubles_the_pause_while_429_comes_without_retry_after()
    {
        await using var run = PollingRun.Start([_ => TooManyRequests(), _ => TooManyRequests(), _ => TooManyRequests(), _ => Ok("a"), _ => Ok("b")]);

        await run.PollsAsync(5);

        var gaps = run.Gaps();
        AssertGap(_interval * 2, gaps[0]);
        AssertGap(_interval * 4, gaps[1]);
        AssertGap(_interval * 8, gaps[2]);
        AssertGap(_interval, gaps[3]);
    }

    [Fact]
    public async Task Caps_a_retry_after_that_asks_for_too_long()
    {
        await using var run = PollingRun.Start([_ => TooManyRequests(TimeSpan.FromDays(1)), _ => Ok("a")]);

        await run.PollsAsync(2);

        AssertGap(HttpPollingSource.MaxThrottle, run.Gaps()[0]);
    }

    [Fact]
    public async Task Keeps_the_schedule_on_server_errors_without_retry_after()
    {
        await using var run = PollingRun.Start([_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Ok("a")]);

        await run.PollsAsync(2);

        AssertGap(_interval, run.Gaps()[0]);
    }

    [Fact]
    public async Task Logs_an_outage_once_when_it_starts_and_once_when_it_ends()
    {
        var logger = new FakeLogger<HttpPollingSource>();
        var serverError = (HttpRequestMessage _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await using var run = PollingRun.Start(
            [_ => Ok("a"), serverError, serverError, serverError, _ => Ok("b"), _ => Ok("c"), _ => Ok("d")],
            logger: logger);

        // The episode ends at the first success a quiet period after the last failure: the second success here.
        await run.PollsAsync(7);

        var logs = logger.Collector.GetSnapshot();
        var failing = Assert.Single(logs, log => log.Level == LogLevel.Warning);
        Assert.Contains("HTTP 500", failing.Message, StringComparison.Ordinal);
        var recovered = Assert.Single(logs, log => log.Level == LogLevel.Information);
        Assert.Contains("3 polls failed", recovered.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The test advances time in steps of one interval, so a pause shows up rounded up to the next step: at least as
    /// long as expected and less than one step longer.
    /// </summary>
    private static void AssertGap(TimeSpan expected, TimeSpan actual) =>
        Assert.True(actual >= expected && actual < expected + _interval, $"Expected a pause of {expected}, got {actual}.");

    private static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter = null) => Rejected(HttpStatusCode.TooManyRequests, retryAfter);

    private static HttpResponseMessage Rejected(HttpStatusCode status, TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    private static HttpResponseMessage Ok(string body, string? etag = null, DateTimeOffset? lastModified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        response.Content.Headers.LastModified = lastModified;
        return response;
    }

    private static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    /// <summary>Answers requests from a script in order; every request past the script gets 304.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage>[] script, TimeProvider time) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public ConcurrentQueue<DateTimeOffset> SentAt { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Requests.Count;
            SentAt.Enqueue(time.GetUtcNow());
            Requests.Enqueue(request);
            return Task.FromResult(number < script.Length ? script[number](request) : NotModified());
        }
    }

    /// <summary>A running source on fake time, with its requests and output.</summary>
    private sealed class PollingRun : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new();
        private readonly Channel<RawRecord> _output = Channel.CreateUnbounded<RawRecord>();
        private readonly CancellationTokenSource _stop = new();
        private readonly HttpPollingSource _source;

        private PollingRun(Func<HttpRequestMessage, HttpResponseMessage>[] script, HttpFeedOptions feed, ILogger<HttpPollingSource> logger)
        {
            StartedAt = _time.GetUtcNow();
            Handler = new ScriptedHandler(script, _time);
            feed.Interval = _interval;
            _source = new HttpPollingSource(feed, Handler, _time, logger);
            Task = _source.RunAsync(_output.Writer, _stop.Token);
        }

        public ScriptedHandler Handler { get; }

        public DateTimeOffset StartedAt { get; }

        public Task Task { get; }

        public ChannelReader<RawRecord> Output => _output.Reader;

        public static PollingRun Start(
            Func<HttpRequestMessage, HttpResponseMessage>[] script,
            HttpFeedOptions? feed = null,
            ILogger<HttpPollingSource>? logger = null) =>
            new(
                script,
                feed ?? new HttpFeedOptions { FeedId = "test", Url = "https://example.test/feed" },
                logger ?? NullLogger<HttpPollingSource>.Instance);

        /// <summary>Time between consecutive requests, in order.</summary>
        public List<TimeSpan> Gaps()
        {
            var sent = Handler.SentAt.ToList();
            return sent.Zip(sent.Skip(1), (earlier, later) => later - earlier).ToList();
        }

        /// <summary>
        /// Advances time until at least <paramref name="count"/> requests were sent. Polls run one after another, so
        /// once request N+1 is out, everything poll N produced is already in the output.
        /// </summary>
        public async Task PollsAsync(int count)
        {
            var waited = Stopwatch.StartNew();
            while (Handler.Requests.Count < count)
            {
                if (Task.IsCompleted)
                {
                    await Task;
                }

                Assert.True(waited.Elapsed < _timeout, $"Only {Handler.Requests.Count} of {count} polls happened.");

                // The source may not be waiting yet when time moves; then it finds the interval already over and polls at once.
                _time.Advance(_interval);
                await System.Threading.Tasks.Task.Delay(10, Ct);
            }
        }

        public List<RawRecord> Records()
        {
            var records = new List<RawRecord>();
            while (_output.Reader.TryRead(out var record))
            {
                records.Add(record);
            }

            return records;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task);
            _source.Dispose();
            _stop.Dispose();
        }
    }
}

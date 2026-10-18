using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
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
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Requests.Count;
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

        private PollingRun(Func<HttpRequestMessage, HttpResponseMessage>[] script, HttpFeedOptions feed)
        {
            StartedAt = _time.GetUtcNow();
            Handler = new ScriptedHandler(script);
            feed.Interval = _interval;
            _source = new HttpPollingSource(feed, Handler, _time, NullLogger<HttpPollingSource>.Instance);
            Task = _source.RunAsync(_output.Writer, _stop.Token);
        }

        public ScriptedHandler Handler { get; }

        public DateTimeOffset StartedAt { get; }

        public Task Task { get; }

        public ChannelReader<RawRecord> Output => _output.Reader;

        public static PollingRun Start(Func<HttpRequestMessage, HttpResponseMessage>[] script, HttpFeedOptions? feed = null) =>
            new(script, feed ?? new HttpFeedOptions { FeedId = "test", Url = "https://example.test/feed" });

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

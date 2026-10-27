using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Channels;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Http;

/// <summary>
/// A feed fetched by periodic HTTP GET: GTFS-RT snapshots, SIRI responses, weather. Every changed response body is
/// passed on unchanged as one <see cref="RawRecord"/>; a 304 answer or a body identical to the previous one is skipped.
/// </summary>
/// <remarks>
/// A failed poll (network error, timeout, error status, oversized body) does not end the run: polls are independent,
/// and a restart would forget the validators and the last hash. The next poll runs on schedule, since a later one
/// would only widen the gap in the archive; only a server that asks to slow down (429, or <c>Retry-After</c> on 503)
/// is polled less often. Failures are logged once per episode, not once per poll.
/// </remarks>
internal sealed partial class HttpPollingSource : IFeedSource, IDisposable
{
    /// <summary>
    /// Placeholder in <see cref="HttpFeedOptions.Url"/> replaced by a new id at start and after a poll whose response
    /// may have been lost.
    /// </summary>
    public const string RequestorIdPlaceholder = "{requestorId}";

    /// <summary>Longest pause a server can ask for: a wrong <c>Retry-After</c> must not stop a feed for hours.</summary>
    internal static readonly TimeSpan MaxThrottle = TimeSpan.FromMinutes(30);

    private readonly HttpFeedOptions _feed;
    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpPollingSource> _logger;
    private readonly FailureEpisodes _failures = new(FailureEpisodes.DefaultQuietPeriod);

    private string _url;
    private EntityTagHeaderValue? _etag;
    private DateTimeOffset? _lastModified;
    private byte[]? _lastHash;

    // Pause before the next poll that the server asked for; null polls on the configured interval.
    private TimeSpan? _throttle;

    /// <summary>Creates the source; it owns <paramref name="handler"/> and disposes it.</summary>
    /// <param name="feed">Feed settings.</param>
    /// <param name="handler">HTTP handler: a real connection pool in production, a fake in tests.</param>
    /// <param name="timeProvider">Clock for receive times and the poll schedule.</param>
    /// <param name="logger">Feed log.</param>
    public HttpPollingSource(HttpFeedOptions feed, HttpMessageHandler handler, TimeProvider timeProvider, ILogger<HttpPollingSource> logger)
    {
        _feed = feed;

        // SIRI incremental requests: the first poll with a new id returns a full snapshot, later ones only changes.
        // A fresh id per run puts a full snapshot into the archive after every restart, so state can be rebuilt from it.
        _url = NewRequestorUrl();
        _timeProvider = timeProvider;
        _logger = logger;
        _client = new HttpClient(handler)
        {
            Timeout = feed.Timeout,
            MaxResponseContentBufferSize = feed.MaxResponseBytes,
        };
        foreach (var (name, value) in feed.Headers)
        {
            _client.DefaultRequestHeaders.Add(name, value);
        }
    }

    /// <inheritdoc />
    public string FeedId => _feed.FeedId;

    /// <inheritdoc />
    public TimeSpan? MaxSilence => _feed.MaxSilence;

    /// <inheritdoc />
    public async Task RunAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        while (true)
        {
            var started = _timeProvider.GetTimestamp();
            var record = await PollAsync(cancellationToken);
            if (record is not null)
            {
                await output.WriteAsync(record, cancellationToken);
            }

            // Keeps the cadence: a slow poll shortens the pause after it, so polls do not drift later and later.
            var wait = (_throttle ?? _feed.Interval) - _timeProvider.GetElapsedTime(started);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>Fetches the feed once.</summary>
    /// <returns>The response as a record, or <see langword="null"/> when nothing changed or the poll failed.</returns>
    private async Task<RawRecord?> PollAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (_etag is not null)
        {
            request.Headers.IfNoneMatch.Add(_etag);
        }

        request.Headers.IfModifiedSince = _lastModified;

        try
        {
            // The default completion option reads the whole body here, so the limit and the timeout cover it too.
            using var response = await _client.SendAsync(request, cancellationToken);
            var receivedAt = _timeProvider.GetUtcNow();
            var previousThrottle = _throttle;
            _throttle = null;
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotModified)
            {
                Throttle(response, receivedAt, previousThrottle);

                // A 4xx (429 included) rejects the request before any data is produced; a 5xx may come after it.
                PollFailed($"HTTP {(int)response.StatusCode}", renewRequestorId: (int)response.StatusCode >= 500);
                return null;
            }

            PollSucceeded(receivedAt);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }

            _etag = response.Headers.ETag;
            _lastModified = response.Content.Headers.LastModified;
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // Not every server supports validators, and some answer every request anew with the same content.
            var hash = SHA256.HashData(payload);
            if (_lastHash is not null && hash.AsSpan().SequenceEqual(_lastHash))
            {
                return null;
            }

            _lastHash = hash;
            return new RawRecord(FeedId, receivedAt, payload, Topic: null);
        }
        catch (HttpRequestException e)
        {
            PollFailed(e.Message, renewRequestorId: true);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            PollFailed($"no response within {_feed.Timeout:g}", renewRequestorId: true);
            return null;
        }
    }

    /// <summary>
    /// Sets the pause before the next poll when the server asks to slow down: as long as <c>Retry-After</c> says,
    /// or, for a 429 without it, twice the previous pause. Kept within the interval and <see cref="MaxThrottle"/>.
    /// </summary>
    private void Throttle(HttpResponseMessage response, DateTimeOffset receivedAt, TimeSpan? previous)
    {
        var retryAfter = response.Headers.RetryAfter;
        var asked = retryAfter?.Delta ?? (retryAfter?.Date - receivedAt);
        if (asked is null && response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return;
        }

        var wait = asked ?? (previous ?? _feed.Interval) * 2;
        _throttle = wait < _feed.Interval ? _feed.Interval : wait > MaxThrottle ? MaxThrottle : wait;
    }

    private void PollFailed(string reason, bool renewRequestorId)
    {
        var starts = _failures.Fail(FeedId, _timeProvider.GetUtcNow());
        if (starts)
        {
            LogPollsFailing(FeedId, reason);
        }

        if (renewRequestorId)
        {
            RenewRequestorId(log: starts);
        }
    }

    private void PollSucceeded(DateTimeOffset at)
    {
        if (_failures.Succeed(FeedId, at) is var failed and > 0)
        {
            LogPollsRecovered(FeedId, failed);
        }
    }

    private string NewRequestorUrl() =>
        _feed.Url.Replace(RequestorIdPlaceholder, Guid.NewGuid().ToString(), StringComparison.Ordinal);

    /// <summary>
    /// Starts a new SIRI subscription after a poll whose response may have been produced but not received: the server
    /// counts those changes as delivered, so the same id would never return them. A new id returns a full snapshot.
    /// </summary>
    private void RenewRequestorId(bool log)
    {
        if (_feed.Url.Contains(RequestorIdPlaceholder, StringComparison.Ordinal))
        {
            _url = NewRequestorUrl();

            // Validators belong to the old URL's responses; the new subscription starts without them.
            _etag = null;
            _lastModified = null;
            if (log)
            {
                LogRequestorIdRenewed(FeedId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polls of feed {FeedId} fail: {Reason}; polling continues, failures are counted until it recovers")]
    private partial void LogPollsFailing(string feedId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {FeedId} polls again; {Failed} polls failed")]
    private partial void LogPollsRecovered(string feedId, long failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {FeedId}: requestorId renewed after a failed poll; the next response is a full snapshot")]
    private partial void LogRequestorIdRenewed(string feedId);
}

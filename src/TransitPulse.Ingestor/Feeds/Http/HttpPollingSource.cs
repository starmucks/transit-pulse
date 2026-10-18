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
/// A failed poll (network error, timeout, error status, oversized body) is logged and the next poll runs on schedule
/// instead of ending the run: polls are independent, and a restart would forget the validators and the last hash.
/// </remarks>
internal sealed partial class HttpPollingSource : IFeedSource, IDisposable
{
    /// <summary>Placeholder in <see cref="HttpFeedOptions.Url"/> replaced by an id unique to this process run.</summary>
    public const string RequestorIdPlaceholder = "{requestorId}";

    private readonly HttpFeedOptions _feed;
    private readonly string _url;
    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpPollingSource> _logger;

    private EntityTagHeaderValue? _etag;
    private DateTimeOffset? _lastModified;
    private byte[]? _lastHash;

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
        _url = feed.Url.Replace(RequestorIdPlaceholder, Guid.NewGuid().ToString(), StringComparison.Ordinal);
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
            var wait = _feed.Interval - _timeProvider.GetElapsedTime(started);
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
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogPollFailed(FeedId, $"HTTP {(int)response.StatusCode}");
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
            LogPollFailed(FeedId, e.Message);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogPollFailed(FeedId, $"no response within {_feed.Timeout:g}");
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Poll of feed {FeedId} failed: {Reason}; next poll on schedule")]
    private partial void LogPollFailed(string feedId, string reason);
}

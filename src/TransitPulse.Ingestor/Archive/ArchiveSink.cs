using System.Globalization;
using System.Runtime.ExceptionServices;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Raw archive: one Parquet file per feed and UTC hour, laid out as
/// <c>{root}/{feedId}/{yyyy-MM-dd}/{feedId}-{yyyyMMdd}T{HH}Z-{n}.parquet</c>.
/// Hours come from <see cref="RawRecord.ReceivedAt"/>, not the system clock, so replays lay files out the same way.
/// </summary>
/// <param name="rootDirectory">Archive root.</param>
/// <param name="rowGroupMaxBytes">Passed to every <see cref="ArchiveFile"/>.</param>
/// <param name="logger">Archive log.</param>
internal sealed partial class ArchiveSink(string rootDirectory, long rowGroupMaxBytes, ILogger<ArchiveSink> logger) : IRawSink
{
    private readonly Dictionary<string, (ArchiveFile File, DateTimeOffset Hour)> _open = new(StringComparer.Ordinal);
    private DateTimeOffset _latestHour = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public async ValueTask WriteAsync(RawRecord record, CancellationToken cancellationToken)
    {
        var hour = TruncateToHour(record.ReceivedAt);
        if (hour > _latestHour)
        {
            _latestHour = hour;
            // Any feed reaching a new hour closes the previous hour of every feed, so quiet feeds
            // (alerts, weather) do not keep a finished hour open and away from sync.
            await CompleteOlderThanAsync(hour, cancellationToken);
        }

        if (!_open.TryGetValue(record.FeedId, out var open))
        {
            // _latestHour, not the record's own hour: a late record never reopens a finished hour.
            open = (await CreateFileAsync(record.FeedId, _latestHour, cancellationToken), _latestHour);
            _open.Add(record.FeedId, open);
        }

        await open.File.AddAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(CancellationToken cancellationToken) =>
        await CompleteOlderThanAsync(DateTimeOffset.MaxValue, cancellationToken);

    private async Task CompleteOlderThanAsync(DateTimeOffset hour, CancellationToken cancellationToken)
    {
        // One file failing to close must not cost the other feeds their hour: close every file, then report.
        List<Exception>? failures = null;
        foreach (var (feedId, open) in _open.Where(entry => entry.Value.Hour < hour).ToList())
        {
            // Removed first: a file that failed to close stays .tmp and is not retried.
            _open.Remove(feedId);
            try
            {
                await open.File.CompleteAsync(cancellationToken);
                LogFileCompleted(open.File.Path, open.File.RecordCount);
            }
            catch (Exception ex)
            {
                LogFileFailed(ex, open.File.Path);
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures is not null)
        {
            throw new AggregateException("Several archive files failed to close.", failures);
        }
    }

    private async Task<ArchiveFile> CreateFileAsync(string feedId, DateTimeOffset hour, CancellationToken cancellationToken)
    {
        if (!FeedIds.IsValid(feedId))
        {
            throw new ArgumentException($"Feed id '{feedId}' must contain {FeedIds.Rule}.", nameof(feedId));
        }

        var directory = Path.Combine(rootDirectory, feedId, hour.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        // A finished Parquet file cannot be appended to: after a restart within the same hour, take the next free number.
        for (var n = 0; ; n++)
        {
            var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{feedId}-{hour:yyyyMMdd'T'HH'Z'}-{n}.parquet"));
            if (!File.Exists(path) && !File.Exists(path + ".tmp"))
            {
                return await ArchiveFile.CreateAsync(path, rowGroupMaxBytes, cancellationToken);
            }
        }
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Archive file {Path} completed with {RecordCount} records")]
    private partial void LogFileCompleted(string path, long recordCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Archive file {Path} failed to close; its records stay in the .tmp file")]
    private partial void LogFileFailed(Exception exception, string path);
}

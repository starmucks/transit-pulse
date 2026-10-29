using System.Globalization;
using System.Runtime.CompilerServices;
using Parquet;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Reads raw records back from the archive for replay: the given feeds over a time range, merged across feeds in
/// order of receive time. Within a feed the stored order is kept, which is the order the live pipeline saw. Files are
/// read one row group at a time, so memory holds one group per feed (bounded by <c>Archive:RowGroupMaxBytes</c> of
/// payload), never a whole hour.
/// </summary>
internal static class ArchiveReader
{
    /// <summary>Streams the records of <paramref name="feedIds"/> received in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    /// <param name="rootDirectory">Archive root in the <see cref="ArchiveSink"/> layout.</param>
    /// <param name="feedIds">Feeds to read; a feed without files in the range yields nothing.</param>
    /// <param name="from">Start of the range, inclusive.</param>
    /// <param name="to">End of the range, exclusive.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The records in order of receive time; ties keep the order of <paramref name="feedIds"/>.</returns>
    /// <exception cref="InvalidDataException">A file in the range cannot be read: a replay must not lose records silently.</exception>
    public static async IAsyncEnumerable<RawRecord> ReadAsync(
        string rootDirectory,
        IReadOnlyList<string> feedIds,
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A k-way merge: one head record per feed, the earliest goes out next.
        var feeds = new List<IAsyncEnumerator<RawRecord>>(feedIds.Count);
        var heads = new PriorityQueue<IAsyncEnumerator<RawRecord>, (DateTimeOffset ReceivedAt, int Feed)>();
        try
        {
            foreach (var feedId in feedIds)
            {
                var feed = ReadFeedAsync(rootDirectory, feedId, from, to, cancellationToken).GetAsyncEnumerator(cancellationToken);
                feeds.Add(feed);
                if (await feed.MoveNextAsync())
                {
                    heads.Enqueue(feed, (feed.Current.ReceivedAt, feeds.Count - 1));
                }
            }

            while (heads.TryDequeue(out var feed, out var head))
            {
                yield return feed.Current;
                if (await feed.MoveNextAsync())
                {
                    heads.Enqueue(feed, (feed.Current.ReceivedAt, head.Feed));
                }
            }
        }
        finally
        {
            foreach (var feed in feeds)
            {
                await feed.DisposeAsync();
            }
        }
    }

    private static async IAsyncEnumerable<RawRecord> ReadFeedAsync(
        string rootDirectory, string feedId, DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();
        foreach (var path in FilesInRange(rootDirectory, feedId, from, to))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920, useAsync: true);
            await using var reader = await OpenAsync(stream, path, cancellationToken);
            for (var i = 0; i < reader.RowGroupCount; i++)
            {
                var (receivedAt, topics, payloads, pastEnd) = await ReadRowGroupAsync(reader, i, path, fromMs, toMs, cancellationToken);
                if (pastEnd)
                {
                    break;
                }

                for (var row = 0; row < receivedAt.Length; row++)
                {
                    // Not a stop condition: receive times within a feed are not strictly increasing.
                    if (receivedAt[row] >= fromMs && receivedAt[row] < toMs)
                    {
                        yield return new RawRecord(feedId, DateTimeOffset.FromUnixTimeMilliseconds(receivedAt[row]), payloads[row] ?? [], topics[row]);
                    }
                }
            }
        }
    }

    // ArchiveSink files every record under the hour of its own receive time, so the hours of the range are enough.
    private static IEnumerable<string> FilesInRange(string rootDirectory, string feedId, DateTimeOffset from, DateTimeOffset to)
    {
        var start = from.ToUniversalTime();
        for (var hour = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero); hour < to; hour = hour.AddHours(1))
        {
            var directory = Path.Combine(rootDirectory, feedId, hour.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            // Several files per hour after a restart (-0, -1, ...); the pattern does not match unfinished *.tmp files.
            var pattern = string.Create(CultureInfo.InvariantCulture, $"{feedId}-{hour:yyyyMMdd'T'HH'Z'}-*.parquet");
            foreach (var path in Directory.EnumerateFiles(directory, pattern).Order(Comparer<string>.Create(ArchiveStats.CompareChronologically)))
            {
                yield return path;
            }
        }
    }

    private static async Task<ParquetReader> OpenAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    // Receive times are read first, so a group outside the range skips decoding its payloads, the bulk of the file.
    // PastEnd: the group starts at or after the end, and so does the rest of the file (a file is written in arrival order).
    private static async Task<(long[] ReceivedAt, string?[] Topics, byte[]?[] Payloads, bool PastEnd)> ReadRowGroupAsync(
        ParquetReader reader, int index, string path, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        try
        {
            using var group = reader.OpenRowGroupReader(index);
            var count = checked((int)group.RowCount);
            var receivedAt = new long[count];
            await group.ReadAsync(ArchiveFile.ReceivedAtField, receivedAt.AsMemory(), null, cancellationToken);
            if (!Array.Exists(receivedAt, time => time >= fromMs && time < toMs))
            {
                return ([], [], [], count > 0 && receivedAt.Min() >= toMs);
            }

            var topics = new string?[count];
            var payloads = new byte[]?[count];
            await group.ReadAsync(ArchiveFile.TopicField, topics.AsMemory(), null, cancellationToken);
            await group.ReadAsync(ArchiveFile.PayloadField, payloads.AsMemory(), null, cancellationToken);
            return (receivedAt, topics, payloads, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"{path}: row group {index}: {ex.Message}; check the file with --stats", ex);
        }
    }
}

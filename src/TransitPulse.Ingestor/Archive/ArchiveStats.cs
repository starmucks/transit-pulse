using System.Globalization;
using Parquet;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Archive inventory, run as <c>TransitPulse.Ingestor --stats &lt;archive&gt;</c>: per feed and UTC day, the files,
/// records, average rate, hours with data and gaps between consecutive receive times, plus every file that cannot be
/// read. Only the receive-time column is read, so a whole archive is scanned without loading payloads.
/// </summary>
/// <remarks>
/// A gap is attributed to the day it ends in. Long gaps are a fault only for continuous feeds (vehicle positions);
/// feeds archived on change (alerts, weather) are quiet for hours by design.
/// </remarks>
internal static class ArchiveStats
{
    /// <summary>Silence longer than this counts as a gap: the stage 1 acceptance criterion for continuous feeds.</summary>
    internal static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(5);

    /// <summary>Prints the inventory of every <c>*.parquet</c> file under <paramref name="archiveDirectory"/>.</summary>
    /// <param name="archiveDirectory">Archive root in the <c>feed/date/name.parquet</c> layout.</param>
    /// <param name="output">Receives the report.</param>
    /// <param name="cancellationToken">Stops the scan between files.</param>
    /// <returns>Process exit code: 0 when every file was read, 1 when any file could not be read.</returns>
    public static async Task<int> RunAsync(string archiveDirectory, TextWriter output, CancellationToken cancellationToken = default)
    {
        var feeds = Directory.EnumerateFiles(archiveDirectory, "*.parquet", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(archiveDirectory, path))
            .GroupBy(FeedOf, StringComparer.Ordinal)
            .OrderBy(feed => feed.Key, StringComparer.Ordinal);
        var unreadable = new List<string>();

        await output.WriteLineAsync($"{"feed",-14} {"day",-10} {"files",5} {"records",12} {"rec/s",8} {"hours",5} {$"gaps>{GapThreshold.TotalMinutes:0}m",9} {"gap min",8}  longest gap");
        foreach (var feed in feeds)
        {
            var days = new SortedDictionary<DateOnly, DayStats>();
            long? previous = null;
            foreach (var relative in feed.Order(Comparer<string>.Create(CompareChronologically)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long[] times;
                try
                {
                    times = await ReadReceivedAtAsync(Path.Combine(archiveDirectory, relative), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unreadable.Add($"{relative}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (times.Length == 0)
                {
                    continue;
                }

                Array.Sort(times);
                DayOf(days, times[0]).Files++;
                foreach (var time in times)
                {
                    var day = DayOf(days, time);
                    day.Records++;
                    day.Hours.Add(DateTimeOffset.FromUnixTimeMilliseconds(time).Hour);
                    if (previous is { } last && time > last)
                    {
                        day.AddGap(last, time - last);
                    }

                    // Max, not assignment: a late record in the next file must not open a false gap behind it.
                    previous = previous is { } p ? Math.Max(p, time) : time;
                }
            }

            foreach (var (date, day) in days)
            {
                var rate = day.Records / (day.Hours.Count * 3600.0);
                var longest = day.LongestGapMs > 0
                    ? $"{TimeSpan.FromMilliseconds(day.LongestGapMs):hh\\:mm\\:ss} from {DateTimeOffset.FromUnixTimeMilliseconds(day.LongestGapStart):HH:mm:ss}"
                    : "-";
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{feed.Key,-14} {date:yyyy-MM-dd} {day.Files,5} {day.Records,12:N0} {rate,8:F2} {day.Hours.Count,5} {day.GapCount,9} {day.GapTotalMs / 60_000.0,8:F1}  {longest}"));
            }
        }

        foreach (var line in unreadable)
        {
            await output.WriteLineAsync($"UNREADABLE {line}");
        }

        return unreadable.Count == 0 ? 0 : 1;
    }

    private static async Task<long[]> ReadReceivedAtAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920, useAsync: true);
        await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
        var groups = new List<long[]>();
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            var values = new long[checked((int)group.RowCount)];
            await group.ReadAsync(ArchiveFile.ReceivedAtField, values.AsMemory(), null, cancellationToken);
            groups.Add(values);
        }

        return groups.Count == 1 ? groups[0] : [.. groups.SelectMany(values => values)];
    }

    private static DayStats DayOf(SortedDictionary<DateOnly, DayStats> days, long unixMs)
    {
        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);
        if (!days.TryGetValue(date, out var day))
        {
            day = new DayStats();
            days.Add(date, day);
        }

        return day;
    }

    // Names are "{feed}-{yyyyMMdd}T{HH}Z-{n}.parquet" under "{feed}/{date}/": ordinal order breaks at n = 10.
    private static int CompareChronologically(string left, string right)
    {
        var (leftHour, leftNumber) = SplitNumber(left);
        var (rightHour, rightNumber) = SplitNumber(right);
        var byHour = string.CompareOrdinal(leftHour, rightHour);
        return byHour != 0 ? byHour : leftNumber.CompareTo(rightNumber);
    }

    private static (string Hour, int Number) SplitNumber(string relative)
    {
        var name = Path.GetFileNameWithoutExtension(relative);
        var dash = name.LastIndexOf('-');
        return dash > 0 && int.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (name[..dash], number)
            : (name, 0);
    }

    private static string FeedOf(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

    private sealed class DayStats
    {
        public int Files { get; set; }

        public long Records { get; set; }

        public HashSet<int> Hours { get; } = [];

        public int GapCount { get; private set; }

        public long GapTotalMs { get; private set; }

        public long LongestGapMs { get; private set; }

        public long LongestGapStart { get; private set; }

        public void AddGap(long start, long lengthMs)
        {
            if (lengthMs <= GapThreshold.TotalMilliseconds)
            {
                return;
            }

            GapCount++;
            GapTotalMs += lengthMs;
            if (lengthMs > LongestGapMs)
            {
                LongestGapMs = lengthMs;
                LongestGapStart = start;
            }
        }
    }
}

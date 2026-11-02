using System.Globalization;
using Parquet;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Daily flow profile, run as <c>TransitPulse.Ingestor --profile &lt;archive&gt; &lt;from&gt; &lt;to&gt;</c> (UTC dates,
/// <c>to</c> exclusive): per feed and hour of the day in the city's local time, the average and highest records per
/// second and the archived megabytes per hour, for weekdays and weekends apart. Hours come from the file names and
/// record counts from the row group metadata, so no column is read.
/// </summary>
/// <remarks>
/// Every UTC hour of the range counts, an hour without a file as zero: choose days without collector outages.
/// Each city is a whole number of hours off UTC, so a UTC hour is exactly one local hour, including on DST days.
/// </remarks>
internal static class ArchiveProfile
{
    // IANA ids for Linux; Windows ids too, because InvariantGlobalization leaves Windows without the IANA mapping.
    // Prefixes match both raw feed ids (mbta-vp) and normalized ones (mbta).
    private static readonly (string Prefix, string Iana, string Windows)[] _zones =
    [
        ("hsl", "Europe/Helsinki", "FLE Standard Time"),
        ("entur", "Europe/Oslo", "W. Europe Standard Time"),
        ("mbta", "America/New_York", "Eastern Standard Time"),
    ];

    /// <summary>Prints the profile of every feed with archive files in the range.</summary>
    /// <param name="archiveDirectory">Archive root in the <c>feed/date/name.parquet</c> layout.</param>
    /// <param name="from">First UTC day of the range.</param>
    /// <param name="to">UTC day after the range.</param>
    /// <param name="output">Receives the report.</param>
    /// <param name="cancellationToken">Stops the scan between files.</param>
    /// <returns>Process exit code: 0 when every file was read, 1 when any file could not be read.</returns>
    public static async Task<int> RunAsync(
        string archiveDirectory, DateOnly from, DateOnly to, TextWriter output, CancellationToken cancellationToken = default)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var feeds = new SortedDictionary<string, Dictionary<DateTimeOffset, (long Records, long Bytes)>>(StringComparer.Ordinal);
        var unreadable = new List<string>();

        foreach (var path in Directory.EnumerateFiles(archiveDirectory, "*.parquet", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseHour(path, out var hour) || hour < start || hour >= end)
            {
                continue;
            }

            var relative = Path.GetRelativePath(archiveDirectory, path);
            long records = 0, bytes;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
                for (var i = 0; i < reader.RowGroupCount; i++)
                {
                    using var group = reader.OpenRowGroupReader(i);
                    records += group.RowCount;
                }

                bytes = stream.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadable.Add($"{relative}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var feed = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (!feeds.TryGetValue(feed, out var hours))
            {
                hours = [];
                feeds.Add(feed, hours);
            }

            // Several files share an hour after a restart.
            var total = hours.GetValueOrDefault(hour);
            hours[hour] = (total.Records + records, total.Bytes + bytes);
        }

        foreach (var (feed, hours) in feeds)
        {
            var zone = ZoneOf(feed);
            var slots = new SlotStats[2, 24];
            for (var hour = start; hour < end; hour = hour.AddHours(1))
            {
                var local = TimeZoneInfo.ConvertTime(hour, zone);
                var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0;
                var (records, bytes) = hours.GetValueOrDefault(hour);
                (slots[weekend, local.Hour] ??= new SlotStats()).Add(records / 3600.0, bytes);
            }

            await output.WriteLineAsync($"{feed} ({zone.Id} local time, UTC {from:yyyy-MM-dd} to {to:yyyy-MM-dd})");
            await output.WriteLineAsync($"{"hour",4} {"days",4} {"rec/s",9} {"max",9} {"MiB/h",8}   {"days",4} {"rec/s",9} {"max",9} {"MiB/h",8}   (weekdays | weekends)");
            for (var slot = 0; slot < 24; slot++)
            {
                await output.WriteLineAsync($"{slot,4:00} {Format(slots[0, slot])}   {Format(slots[1, slot])}");
            }
        }

        foreach (var line in unreadable)
        {
            await output.WriteLineAsync($"UNREADABLE {line}");
        }

        return unreadable.Count == 0 ? 0 : 1;
    }

    private static string Format(SlotStats? slot) => slot is null
        ? $"{0,4} {"-",9} {"-",9} {"-",8}"
        : string.Create(CultureInfo.InvariantCulture,
            $"{slot.Count,4} {slot.RateSum / slot.Count,9:F2} {slot.RateMax,9:F2} {slot.Extra / 1048576.0 / slot.Count,8:F1}");

    // Names are "{feed}-{yyyyMMdd}T{HH}Z-{n}.parquet"; the hour is the UTC hour the records were received in.
    private static bool TryParseHour(string path, out DateTimeOffset hour)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        hour = default;
        return dash >= 12 && DateTimeOffset.TryParseExact(
            name.AsSpan(dash - 12, 12), "yyyyMMdd'T'HH'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour);
    }

    // Feeds of no single city (weather) stay in UTC.
    internal static TimeZoneInfo ZoneOf(string feed)
    {
        foreach (var (prefix, iana, windows) in _zones)
        {
            if (feed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var zone) ? zone : TimeZoneInfo.FindSystemTimeZoneById(windows);
            }
        }

        return TimeZoneInfo.Utc;
    }

    // One hour of the day for one day type: a sample per day with its rate, plus a second amount summed (bytes here).
    internal sealed class SlotStats
    {
        public int Count { get; private set; }

        public double RateSum { get; private set; }

        public double RateMax { get; private set; }

        public long Extra { get; private set; }

        public void Add(double rate, long extra)
        {
            Count++;
            RateSum += rate;
            RateMax = Math.Max(RateMax, rate);
            Extra += extra;
        }
    }
}

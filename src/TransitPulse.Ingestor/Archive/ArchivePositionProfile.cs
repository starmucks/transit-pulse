using System.Diagnostics.Metrics;
using System.Globalization;
using TransitPulse.Ingestor.Normalization;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Daily position profile, run as <c>TransitPulse.Ingestor --profile-positions &lt;archive&gt; &lt;from&gt; &lt;to&gt;</c>
/// (UTC dates, <c>to</c> exclusive): reads the raw feeds of every normalizer and passes them through the same
/// <see cref="PositionRouter"/> as the live pipeline, without Kafka. Prints per raw feed and local hour of the day the
/// positions per second it would publish and the share it drops as duplicates, for weekdays and weekends apart.
/// </summary>
/// <remarks>
/// Positions count in the hour their record was received. Every hour of the range counts, an hour without records as
/// zero, as in <see cref="ArchiveProfile"/>. Unlike that profile it reads every payload, so a week takes minutes.
/// </remarks>
internal static partial class ArchivePositionProfile
{
    // Names of PositionMetrics' instrument and tag: the router already counts duplicates, the profile only listens.
    private const string DroppedInstrument = "transitpulse.positions.dropped";
    private const string ReasonTag = "reason";

    /// <summary>Prints the position profile of every raw feed that has a normalizer.</summary>
    /// <param name="archiveDirectory">Archive root in the <see cref="ArchiveSink"/> layout.</param>
    /// <param name="from">First UTC day of the range.</param>
    /// <param name="to">UTC day after the range.</param>
    /// <param name="output">Receives the report; progress and normalizer warnings go to the standard error.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>Process exit code: 0 when the whole range was read, 1 when a file could not be read.</returns>
    public static async Task<int> RunAsync(
        string archiveDirectory, DateOnly from, DateOnly to, TextWriter output, CancellationToken cancellationToken = default)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await using var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace))
            .AddPositionRouter()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<PositionRouter>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ArchivePositionProfile).FullName!);
        var rawFeeds = provider.GetServices<IPositionNormalizer>()
            .SelectMany(normalizer => normalizer.RawFeedIds)
            .Order(StringComparer.Ordinal)
            .ToList();

        long duplicates = 0;
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory) && instrument.Name == DroppedInstrument)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: ReasonTag, Value: PositionMetrics.Duplicate })
                {
                    duplicates += value;
                }
            }
        });
        listener.Start();

        var feeds = rawFeeds.ToDictionary(feed => feed, _ => new Dictionary<DateTimeOffset, (long Positions, long Duplicates)>(), StringComparer.Ordinal);
        var day = DateOnly.MinValue;
        long records = 0;
        try
        {
            await foreach (var record in ArchiveReader.ReadAsync(archiveDirectory, rawFeeds, start, end, cancellationToken))
            {
                // The router reports a record's duplicates synchronously, so the counter's change belongs to this record.
                var before = duplicates;
                var positions = router.Route(record).Count;
                var hour = new DateTimeOffset(record.ReceivedAt.UtcTicks - (record.ReceivedAt.UtcTicks % TimeSpan.TicksPerHour), TimeSpan.Zero);
                var hours = feeds[record.FeedId];
                var total = hours.GetValueOrDefault(hour);
                hours[hour] = (total.Positions + positions, total.Duplicates + duplicates - before);

                records++;
                if (DateOnly.FromDateTime(hour.UtcDateTime) is var recordDay && recordDay != day)
                {
                    LogDayStarted(logger, recordDay, records);
                    day = recordDay;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            // A profile with a silently missing file would understate the flow: no partial report.
            await output.WriteLineAsync($"UNREADABLE {ex.Message}");
            return 1;
        }

        foreach (var (feed, hours) in feeds)
        {
            var zone = ArchiveProfile.ZoneOf(feed);
            var slots = new ArchiveProfile.SlotStats[2, 24];
            for (var hour = start; hour < end; hour = hour.AddHours(1))
            {
                var local = TimeZoneInfo.ConvertTime(hour, zone);
                var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0;
                var (positions, dropped) = hours.GetValueOrDefault(hour);
                (slots[weekend, local.Hour] ??= new ArchiveProfile.SlotStats()).Add(positions / 3600.0, dropped);
            }

            await output.WriteLineAsync($"{feed} positions ({zone.Id} local time, UTC {from:yyyy-MM-dd} to {to:yyyy-MM-dd})");
            await output.WriteLineAsync($"{"hour",4} {"days",4} {"pos/s",9} {"max",9} {"dup%",6}   {"days",4} {"pos/s",9} {"max",9} {"dup%",6}   (weekdays | weekends)");
            for (var slot = 0; slot < 24; slot++)
            {
                await output.WriteLineAsync($"{slot,4:00} {Format(slots[0, slot])}   {Format(slots[1, slot])}");
            }
        }

        return 0;
    }

    // Duplicates as a share of all positions that reached the deduplicator: published plus dropped as repeats.
    private static string Format(ArchiveProfile.SlotStats? slot)
    {
        if (slot is null)
        {
            return $"{0,4} {"-",9} {"-",9} {"-",6}";
        }

        var published = slot.RateSum * 3600;
        var share = published + slot.Extra > 0 ? (100.0 * slot.Extra / (published + slot.Extra)).ToString("F1", CultureInfo.InvariantCulture) : "-";
        return string.Create(CultureInfo.InvariantCulture,
            $"{slot.Count,4} {slot.RateSum / slot.Count,9:F2} {slot.RateMax,9:F2} {share,6}");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Profiling {Day:yyyy-MM-dd}; {Records} records read so far")]
    private static partial void LogDayStarted(ILogger logger, DateOnly day, long records);
}

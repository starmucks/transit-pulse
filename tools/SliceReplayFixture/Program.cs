using System.Globalization;
using SliceReplayFixture;
using TransitPulse.Ingestor.Archive;

// Builds the committed Replay.Tests fixture: one HSL route for one archive hour + a matching GTFS zip slice.
// Usage:
//   dotnet run --project tools/SliceReplayFixture -- <archiveRoot> <gtfsZip> <outputDir> [routeId...]
// Defaults target the author's local full archive on D: and tram route 1009 (line 9).

var archiveRoot = args.ElementAtOrDefault(0) ?? @"D:\TransitPulse\archive";
var gtfsZip = args.ElementAtOrDefault(1) ?? @"D:\TransitPulse\gtfs\hsl\2026-11-03T1433Z.zip";
var outputDir = args.ElementAtOrDefault(2)
    ?? Path.GetFullPath(Path.Combine("tests", "TransitPulse.Replay.Tests", "Fixtures", "hsl-tram-9-hour"));
var routes = args.Length > 3
    ? args[3..].ToHashSet(StringComparer.Ordinal)
    : new HashSet<string>(StringComparer.Ordinal) { "1009" };

var from = new DateTimeOffset(2026, 11, 4, 6, 0, 0, TimeSpan.Zero);
var to = from.AddHours(1);

Console.WriteLine($"Archive: {archiveRoot}");
Console.WriteLine($"GTFS:    {gtfsZip}");
Console.WriteLine($"Output:  {outputDir}");
Console.WriteLine($"Routes:  {string.Join(", ", routes.Order(StringComparer.Ordinal))}");
Console.WriteLine($"Range:   [{from:o}, {to:o})");

Directory.CreateDirectory(outputDir);
var archiveOut = Path.Combine(outputDir, "archive");
if (Directory.Exists(archiveOut))
{
    Directory.Delete(archiveOut, recursive: true);
}

Directory.CreateDirectory(archiveOut);

var kept = 0L;
var seen = 0L;
await using (var file = await OpenArchiveFileAsync(archiveOut, "hsl", from))
{
    await foreach (var record in ArchiveReader.ReadAsync(archiveRoot, ["hsl"], from, to))
    {
        seen++;
        if (!TopicRoute(record.Topic, out var route) || !routes.Contains(route))
        {
            continue;
        }

        await file.AddAsync(record, CancellationToken.None);
        kept++;
        if (kept % 50_000 == 0)
        {
            Console.WriteLine($"  kept {kept:N0} / seen {seen:N0}");
        }
    }

    await file.CompleteAsync(CancellationToken.None);
    Console.WriteLine($"Archive slice: kept {kept:N0} of {seen:N0} records → {file.Path}");
}

if (kept == 0)
{
    throw new InvalidOperationException("No records matched the route filter; check archive path and route ids.");
}

// Processor loads today ± 1 in the feed time zone; keep those three service days only.
var serviceDays = new DateOnly[] { new(2026, 11, 3), new(2026, 11, 4), new(2026, 11, 5) };
var gtfsOut = Path.Combine(outputDir, "gtfs");
GtfsSlicer.Slice(gtfsZip, gtfsOut, routes, serviceDays);
Console.WriteLine($"GTFS slice → {gtfsOut}");
Console.WriteLine("Done.");

// HFP topic route id is segment 9: /hfp/v2/journey/ongoing/vp/{mode}/{op}/{veh}/{route}/…
static bool TopicRoute(string? topic, out string route)
{
    route = "";
    if (string.IsNullOrEmpty(topic))
    {
        return false;
    }

    var parts = topic.Split('/');
    if (parts.Length <= 9 || string.IsNullOrEmpty(parts[9]))
    {
        return false;
    }

    route = parts[9];
    return true;
}

static async Task<ArchiveFile> OpenArchiveFileAsync(string root, string feedId, DateTimeOffset hour)
{
    var directory = Path.Combine(root, feedId, hour.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(
        directory,
        string.Create(CultureInfo.InvariantCulture, $"{feedId}-{hour:yyyyMMdd'T'HH'Z'}-0.parquet"));
    return await ArchiveFile.CreateAsync(path, rowGroupMaxBytes: 16 * 1024 * 1024, CancellationToken.None);
}

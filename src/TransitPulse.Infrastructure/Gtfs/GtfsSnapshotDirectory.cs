using System.Globalization;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// The folder of downloaded GTFS snapshots, <c>{root}/{feed}/{yyyy-MM-dd}T{HHmm}Z.zip</c>. A snapshot is complete once its
/// non-empty <c>.zip.sha256</c> exists: the collector's snapshot script writes it last, and the PC copies it last.
/// </summary>
public static class GtfsSnapshotDirectory
{
    private const string StampFormat = "yyyy-MM-dd'T'HHmm'Z'";

    /// <summary>The newest complete snapshot of every feed folder under the root.</summary>
    /// <param name="root">Snapshot root; a missing folder has no snapshots.</param>
    /// <returns>One entry per feed that has a complete snapshot, ordered by feed id.</returns>
    public static IReadOnlyList<GtfsSnapshotFile> Latest(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var latest = new List<GtfsSnapshotFile>();
        foreach (var feed in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            // A sequence of references: MaxBy over value tuples would throw on a feed with no complete snapshot.
            var newest = Directory.EnumerateFiles(feed)
                .Select(path => TakenAt(path) is { } taken && IsComplete(path) ? new GtfsSnapshotFile(Path.GetFileName(feed), path, taken) : null)
                .OfType<GtfsSnapshotFile>()
                .MaxBy(file => file.TakenAt);
            if (newest is not null)
            {
                latest.Add(newest);
            }
        }

        return latest;
    }

    // Only names the snapshot script writes count: a hand-copied "hsl.zip" or a ".download" in progress does not.
    private static DateTimeOffset? TakenAt(string path) =>
        Path.GetExtension(path) == ".zip"
        && DateTimeOffset.TryParseExact(Path.GetFileNameWithoutExtension(path), StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var taken)
            ? taken
            : null;

    private static bool IsComplete(string zip)
    {
        var sha256 = new FileInfo(zip + ".sha256");
        return sha256.Exists && sha256.Length > 0;
    }
}

/// <summary>A complete snapshot found by <see cref="GtfsSnapshotDirectory"/>.</summary>
/// <param name="FeedId">Feed folder name, e.g. <c>hsl</c>.</param>
/// <param name="Path">Full path of the zip.</param>
/// <param name="TakenAt">Download time from the file name, UTC.</param>
public sealed record GtfsSnapshotFile(string FeedId, string Path, DateTimeOffset TakenAt);

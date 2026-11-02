using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Infrastructure.Tests.Gtfs;

public sealed class GtfsSnapshotDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gtfs-snapshots-").FullName;

    [Fact]
    public void Newest_complete_snapshot_of_each_feed_is_picked()
    {
        Snapshot("hsl", "2026-10-30T1433Z");
        Snapshot("hsl", "2026-11-02T1435Z");
        Snapshot("mbta", "2026-10-31T0232Z");

        var latest = GtfsSnapshotDirectory.Latest(_root);

        Assert.Equal(["hsl", "mbta"], latest.Select(file => file.FeedId));
        Assert.Equal(Path.Combine(_root, "hsl", "2026-11-02T1435Z.zip"), latest[0].Path);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 35, 0, TimeSpan.Zero), latest[0].TakenAt);
    }

    [Fact]
    public void Snapshot_without_its_hash_file_is_not_finished_and_is_skipped()
    {
        Snapshot("hsl", "2026-11-01T1433Z");
        Snapshot("hsl", "2026-11-02T1435Z", sha256: null);
        Snapshot("entur-sky", "2026-11-02T1435Z", sha256: "");

        var latest = GtfsSnapshotDirectory.Latest(_root);

        Assert.Equal(Path.Combine(_root, "hsl", "2026-11-01T1433Z.zip"), Assert.Single(latest).Path);
    }

    [Fact]
    public void Files_the_snapshot_script_does_not_name_are_ignored()
    {
        Snapshot("hsl", "2026-11-01T1433Z");
        Snapshot("hsl", "hsl");
        File.WriteAllText(Path.Combine(_root, "hsl", ".download"), "partial");
        File.WriteAllText(Path.Combine(_root, "hsl", "last.etag"), "\"abc\"");

        Assert.Equal(Path.Combine(_root, "hsl", "2026-11-01T1433Z.zip"), Assert.Single(GtfsSnapshotDirectory.Latest(_root)).Path);
    }

    [Fact]
    public void Missing_root_has_no_snapshots() =>
        Assert.Empty(GtfsSnapshotDirectory.Latest(Path.Combine(_root, "missing")));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Snapshot(string feed, string name, string? sha256 = "0123  name.zip")
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, feed)).FullName;
        File.WriteAllText(Path.Combine(folder, name + ".zip"), "zip");
        if (sha256 is not null)
        {
            File.WriteAllText(Path.Combine(folder, name + ".zip.sha256"), sha256);
        }
    }
}

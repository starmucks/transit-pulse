using System.Globalization;
using System.Text;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveReaderTests : IDisposable
{
    private static readonly DateTimeOffset _day = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-reader-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Merges_feeds_by_receive_time_and_keeps_every_field()
    {
        await WriteAsync("a-20261021T00Z-0", ("00:00:01", "a1", "/hfp/1"), ("00:59:59", "a2", "/hfp/2"));
        await WriteAsync("a-20261021T01Z-0", ("01:00:05", "a3", "/hfp/3"));
        await WriteAsync("b-20261021T00Z-0", ("00:00:01", "b1", null), ("00:30:00", "b2", null));
        await WriteAsync("b-20261021T01Z-0", ("01:00:00", "b3", null));

        var records = await ReadAsync(["a", "b"], "00:00:00", "02:00:00");

        // Equal times keep the order of the requested feeds: a1 before b1.
        Assert.Equal(["a1", "b1", "b2", "a2", "b3", "a3"], records.Select(Payload));
        var a1 = records[0];
        Assert.Equal(("a", At("00:00:01"), "/hfp/1"), (a1.FeedId, a1.ReceivedAt, a1.Topic));
        Assert.Null(records[1].Topic);
    }

    [Fact]
    public async Task Range_includes_its_start_and_excludes_its_end()
    {
        await WriteAsync("a-20261021T00Z-0", ("00:09:59", "before", null), ("00:10:00", "start", null), ("00:19:59", "inside", null), ("00:20:00", "end", null));

        var records = await ReadAsync(["a"], "00:10:00", "00:20:00");

        Assert.Equal(["start", "inside"], records.Select(Payload));
    }

    [Fact]
    public async Task Reads_only_the_requested_feeds()
    {
        await WriteAsync("a-20261021T00Z-0", ("00:00:01", "a1", null));
        await WriteAsync("b-20261021T00Z-0", ("00:00:02", "b1", null));

        var records = await ReadAsync(["b"], "00:00:00", "01:00:00");

        Assert.Equal(["b1"], records.Select(Payload));
    }

    [Fact]
    public async Task Order_holds_across_row_groups()
    {
        // A one-byte limit flushes a row group after every record.
        await WriteAsync("a-20261021T00Z-0", rowGroupMaxBytes: 1, ("00:00:01", "a1", null), ("00:00:02", "a2", null), ("00:00:03", "a3", null));
        await WriteAsync("b-20261021T00Z-0", rowGroupMaxBytes: 1, ("00:00:02", "b1", null));

        var records = await ReadAsync(["a", "b"], "00:00:00", "01:00:00");

        Assert.Equal(["a1", "a2", "b1", "a3"], records.Select(Payload));
    }

    [Fact]
    public async Task Row_groups_outside_the_range_are_skipped_on_both_sides()
    {
        // One group per record: groups before the start are skipped, the first one after the end ends the file.
        await WriteAsync("a-20261021T00Z-0", rowGroupMaxBytes: 1,
            ("00:00:01", "a1", null), ("00:00:02", "a2", null), ("00:00:03", "a3", null), ("00:00:04", "a4", null), ("00:00:05", "a5", null));

        var records = await ReadAsync(["a"], "00:00:02", "00:00:04");

        Assert.Equal(["a2", "a3"], records.Select(Payload));
    }

    [Fact]
    public async Task Files_of_one_hour_are_read_in_number_order()
    {
        // Within a feed the stored order wins over receive time; ordinal order would read -10 before -2.
        await WriteAsync("a-20261021T00Z-2", ("00:10:00", "second", null));
        await WriteAsync("a-20261021T00Z-10", ("00:05:00", "third", null));
        await WriteAsync("a-20261021T00Z-0", ("00:20:00", "first", null));

        var records = await ReadAsync(["a"], "00:00:00", "01:00:00");

        Assert.Equal(["first", "second", "third"], records.Select(Payload));
    }

    [Fact]
    public async Task Missing_feed_hour_or_unfinished_file_yields_nothing()
    {
        await WriteAsync("a-20261021T05Z-0", ("05:00:00", "a1", null));
        var unfinished = Path.Combine(_dir.FullName, "a", "2026-10-21", "a-20261021T00Z-0.parquet.tmp");
        await File.WriteAllBytesAsync(unfinished, [1, 2, 3], Ct);

        Assert.Empty(await ReadAsync(["a", "missing"], "00:00:00", "01:00:00"));
    }

    [Fact]
    public async Task Unreadable_file_fails_with_its_path()
    {
        var broken = Path.Combine(_dir.FullName, "a", "2026-10-21", "a-20261021T00Z-0.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        await File.WriteAllBytesAsync(broken, [1, 2, 3, 4], Ct);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(["a"], "00:00:00", "01:00:00"));

        Assert.StartsWith(broken, error.Message, StringComparison.Ordinal);
    }

    private static DateTimeOffset At(string time) => _day + TimeSpan.Parse(time, CultureInfo.InvariantCulture);

    private static string Payload(RawRecord record) => Encoding.UTF8.GetString(record.Payload);

    private Task WriteAsync(string name, params (string Time, string Payload, string? Topic)[] records) =>
        WriteAsync(name, rowGroupMaxBytes: 1_000_000, records);

    private async Task WriteAsync(string name, long rowGroupMaxBytes, params (string Time, string Payload, string? Topic)[] records)
    {
        var feed = name[..name.IndexOf('-')];
        var directory = Path.Combine(_dir.FullName, feed, "2026-10-21");
        Directory.CreateDirectory(directory);
        var file = await ArchiveFile.CreateAsync(Path.Combine(directory, name + ".parquet"), rowGroupMaxBytes, Ct);
        foreach (var (time, payload, topic) in records)
        {
            await file.AddAsync(new RawRecord(feed, At(time), Encoding.UTF8.GetBytes(payload), topic), Ct);
        }

        await file.CompleteAsync(Ct);
    }

    private async Task<List<RawRecord>> ReadAsync(string[] feeds, string from, string to)
    {
        var records = new List<RawRecord>();
        await foreach (var record in ArchiveReader.ReadAsync(_dir.FullName, feeds, At(from), At(to), Ct))
        {
            records.Add(record);
        }

        return records;
    }
}

using Parquet;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveFileTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 10, 15, 9, 0, 1, 250, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-archive-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Complete_writes_readable_file_and_removes_tmp()
    {
        var path = Path.Combine(_dir.FullName, "hsl.parquet");
        RawRecord[] records =
        [
            new("hsl", _at, [1, 2, 3], "/hfp/v2/journey/ongoing/vp/bus/0022/01234"),
            new("hsl", _at.AddMilliseconds(40), [4], null),
            new("hsl", _at.AddSeconds(1), [], "/hfp/v2/journey/ongoing/vp/tram/0040/00415"),
        ];

        var file = await ArchiveFile.CreateAsync(path, rowGroupMaxBytes: 1_000_000, Ct);
        foreach (var record in records)
        {
            await file.AddAsync(record, Ct);
        }

        await file.CompleteAsync(Ct);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        var (rows, _) = await ReadAsync(path);
        Assert.Equal(records.Select(r => (r.ReceivedAt.ToUnixTimeMilliseconds(), r.Topic, (byte[]?)r.Payload)), rows);
    }

    [Fact]
    public async Task File_is_invisible_until_complete()
    {
        var path = Path.Combine(_dir.FullName, "hsl.parquet");
        await using var file = await ArchiveFile.CreateAsync(path, rowGroupMaxBytes: 1_000_000, Ct);

        await file.AddAsync(new RawRecord("hsl", _at, [1], null), Ct);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Splits_row_groups_by_size()
    {
        var path = Path.Combine(_dir.FullName, "hsl.parquet");
        var file = await ArchiveFile.CreateAsync(path, rowGroupMaxBytes: 10, Ct);
        for (var i = 0; i < 5; i++)
        {
            await file.AddAsync(new RawRecord("hsl", _at.AddSeconds(i), [1, 2, 3, (byte)i], null), Ct);
        }

        await file.CompleteAsync(Ct);

        var (rows, rowGroups) = await ReadAsync(path);
        Assert.True(rowGroups > 1, $"Expected several row groups, got {rowGroups}.");
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public async Task Failed_flush_faults_the_file_and_keeps_it_tmp()
    {
        var path = Path.Combine(_dir.FullName, "in", "hsl.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FailingStream? stream = null;
        var file = await ArchiveFile.CreateAsync(path, rowGroupMaxBytes: 3, s => stream = new FailingStream(s), Ct);
        await file.AddAsync(new RawRecord("hsl", _at, [1, 2, 3], "/hfp/a"), Ct);

        // One failed write midway through a row group, like the transient OutOfMemoryException on the collector.
        stream!.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(
            () => file.AddAsync(new RawRecord("hsl", _at.AddSeconds(1), [4, 5, 6], null), Ct).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => file.AddAsync(new RawRecord("hsl", _at.AddSeconds(2), [7], null), Ct).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => file.CompleteAsync(Ct));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".tmp"));

        // The row group flushed before the failure is still there, and the failed buffer was not written twice.
        var repaired = Path.Combine(_dir.FullName, "out");
        await ArchiveRepair.RunAsync(Path.GetDirectoryName(path)!, repaired, TextWriter.Null, Ct);
        var (rows, _) = await ReadAsync(Path.Combine(repaired, "hsl.parquet"));
        var row = Assert.Single(rows);
        Assert.Equal(_at.ToUnixTimeMilliseconds(), row.ReceivedAt);
        Assert.Equal("/hfp/a", row.Topic);
        Assert.Equal([1, 2, 3], row.Payload);
    }

    private static async Task<(List<(long ReceivedAt, string? Topic, byte[]? Payload)> Rows, int RowGroups)> ReadAsync(string path)
    {
        var rows = new List<(long, string?, byte[]?)>();
        await using var reader = await ParquetReader.CreateAsync(path, cancellationToken: Ct);
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            var count = (int)group.RowCount;
            var receivedAt = new long[count];
            var topics = new string?[count];
            var payloads = new byte[]?[count];
            await group.ReadAsync(ArchiveFile.ReceivedAtField, receivedAt.AsMemory(), null, Ct);
            await group.ReadAsync(ArchiveFile.TopicField, topics.AsMemory(), null, Ct);
            await group.ReadAsync(ArchiveFile.PayloadField, payloads.AsMemory(), null, Ct);
            for (var row = 0; row < count; row++)
            {
                rows.Add((receivedAt[row], topics[row], payloads[row]));
            }
        }

        return (rows, reader.RowGroupCount);
    }
}

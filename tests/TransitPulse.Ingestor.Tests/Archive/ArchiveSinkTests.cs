using Microsoft.Extensions.Logging.Abstractions;
using Parquet;
using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveSinkTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-sink-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Writes_each_feed_to_its_own_hourly_file()
    {
        var sink = CreateSink();

        await sink.WriteAsync(Record("hsl", 9, 10), Ct);
        await sink.WriteAsync(Record("hsl", 9, 11), Ct);
        await sink.WriteAsync(Record("mbta", 9, 10), Ct);
        await sink.CompleteAsync(Ct);

        Assert.Equal(2, await CountRowsAsync(FilePath("hsl", 9, 0)));
        Assert.Equal(1, await CountRowsAsync(FilePath("mbta", 9, 0)));
    }

    [Fact]
    public async Task Closes_previous_hour_of_all_feeds_on_hour_change()
    {
        var sink = CreateSink();

        await sink.WriteAsync(Record("hsl", 9, 59), Ct);
        await sink.WriteAsync(Record("alerts", 9, 30), Ct);
        await sink.WriteAsync(Record("hsl", 10, 0), Ct);

        // The quiet "alerts" feed wrote nothing at 10:00, yet its 09 file is already final.
        Assert.True(File.Exists(FilePath("hsl", 9, 0)));
        Assert.True(File.Exists(FilePath("alerts", 9, 0)));
        Assert.True(File.Exists(FilePath("hsl", 10, 0) + ".tmp"));
        Assert.False(File.Exists(FilePath("hsl", 10, 0)));

        await sink.CompleteAsync(Ct);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_file()
    {
        var beforeRestart = CreateSink();
        await beforeRestart.WriteAsync(Record("hsl", 9, 10), Ct);
        await beforeRestart.CompleteAsync(Ct);

        var afterRestart = CreateSink();
        await afterRestart.WriteAsync(Record("hsl", 9, 20), Ct);
        await afterRestart.WriteAsync(Record("hsl", 9, 21), Ct);
        await afterRestart.CompleteAsync(Ct);

        Assert.Equal(1, await CountRowsAsync(FilePath("hsl", 9, 0)));
        Assert.Equal(2, await CountRowsAsync(FilePath("hsl", 9, 1)));
    }

    [Fact]
    public async Task Late_record_goes_to_current_file()
    {
        var sink = CreateSink();

        await sink.WriteAsync(Record("hsl", 10, 0), Ct);
        await sink.WriteAsync(Record("hsl", 9, 59), Ct);
        await sink.WriteAsync(Record("mbta", 9, 58), Ct);
        await sink.CompleteAsync(Ct);

        Assert.Equal(2, await CountRowsAsync(FilePath("hsl", 10, 0)));
        Assert.Equal(1, await CountRowsAsync(FilePath("mbta", 10, 0)));
        Assert.False(File.Exists(FilePath("hsl", 9, 0)));
        Assert.False(File.Exists(FilePath("mbta", 9, 0)));
    }

    [Fact]
    public async Task Closes_other_feeds_when_one_file_fails_to_close()
    {
        var sink = CreateSink();
        await sink.WriteAsync(Record("hsl", 9, 10), Ct);
        await sink.WriteAsync(Record("mbta", 9, 10), Ct);

        // Something took the final name after the file was opened, so the rename on completion fails.
        await File.WriteAllBytesAsync(FilePath("hsl", 9, 0), [], Ct);

        await Assert.ThrowsAsync<IOException>(() => sink.CompleteAsync(Ct).AsTask());
        Assert.Equal(1, await CountRowsAsync(FilePath("mbta", 9, 0)));
        Assert.True(File.Exists(FilePath("hsl", 9, 0) + ".tmp"));

        // The failed file is not retried.
        await sink.CompleteAsync(Ct);
    }

    [Fact]
    public async Task Failed_write_costs_other_feeds_nothing_and_never_gets_a_final_name()
    {
        // Every 3-byte record fills a row group, so each write flushes.
        FailingStream? hslStream = null;
        var sink = new ArchiveSink(_dir.FullName, rowGroupMaxBytes: 3, NullLogger<ArchiveSink>.Instance, s =>
            s is FileStream { Name: var name } && name.Contains("hsl", StringComparison.Ordinal) ? hslStream = new FailingStream(s) : s);
        await sink.WriteAsync(Record("mbta", 9, 10), Ct);
        await sink.WriteAsync(Record("hsl", 9, 10), Ct);

        // A transient failure: the next flush fails, a retry of the same buffer would succeed.
        hslStream!.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(Record("hsl", 9, 11), Ct).AsTask());

        // What the pipeline does after a write failure: close everything that is still open.
        await sink.CompleteAsync(Ct);

        Assert.Equal(1, await CountRowsAsync(FilePath("mbta", 9, 0)));
        Assert.False(File.Exists(FilePath("hsl", 9, 0)), "a file with a broken row group must stay .tmp");
        Assert.True(File.Exists(FilePath("hsl", 9, 0) + ".tmp"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("HSL")]
    [InlineData("")]
    public async Task Rejects_feed_id_that_is_not_a_safe_path_segment(string feedId)
    {
        var sink = CreateSink();

        await Assert.ThrowsAsync<ArgumentException>(() => sink.WriteAsync(Record(feedId, 9, 10), Ct).AsTask());
    }

    private ArchiveSink CreateSink() => new(_dir.FullName, rowGroupMaxBytes: 1_000_000, NullLogger<ArchiveSink>.Instance);

    private static RawRecord Record(string feedId, int hour, int minute) =>
        new(feedId, new DateTimeOffset(2026, 10, 15, hour, minute, 0, TimeSpan.Zero), [1, 2, 3], null);

    private string FilePath(string feedId, int hour, int n) =>
        Path.Combine(_dir.FullName, feedId, "2026-10-15", $"{feedId}-20261015T{hour:00}Z-{n}.parquet");

    private static async Task<long> CountRowsAsync(string path)
    {
        await using var reader = await ParquetReader.CreateAsync(path, cancellationToken: Ct);
        long rows = 0;
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            rows += group.RowCount;
        }

        return rows;
    }
}

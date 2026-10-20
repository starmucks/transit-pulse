using Parquet;
using Parquet.Schema;
using TransitPulse.Ingestor.Archive;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveRepairTests : IDisposable
{
    private const string OrphanName = "hsl/2026-10-19/hsl-20261019T04Z-1.parquet";

    private static readonly long[] _intactReceivedAt = [1_000, 1_001];
    private static readonly string?[] _intactTopics = ["/hfp/a", null];
    private static readonly byte[][] _intactPayloads = [[1, 2], [3]];
    private static readonly long[] _cutReceivedAt = [2_000];
    private static readonly string?[] _cutTopics = ["/hfp/b"];

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tp-repair-");

    private string Input => Path.Combine(_dir.FullName, "in");

    private string Output => Path.Combine(_dir.FullName, "out");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Keeps_intact_row_groups_and_drops_the_one_cut_by_a_crash()
    {
        var orphan = await WriteOrphanAsync();
        var log = new StringWriter();

        var exitCode = await ArchiveRepair.RunAsync(Input, Output, log, Ct);

        Assert.Equal(0, exitCode);
        var (rows, rowGroups) = await ReadAsync(Path.Combine(Output, OrphanName));
        Assert.Equal(1, rowGroups);
        Assert.Equal(_intactReceivedAt.Select((at, i) => (at, _intactTopics[i], (byte[]?)_intactPayloads[i])), rows);
        Assert.Contains("PARTIAL", log.ToString());
        Assert.True(File.Exists(orphan), "the input must stay untouched");
    }

    [Fact]
    public async Task File_without_footer_fails_and_writes_nothing()
    {
        var orphan = await WriteOrphanAsync();
        await using (var stream = new FileStream(orphan, FileMode.Open))
        {
            stream.SetLength(stream.Length - 20);
        }

        var exitCode = await ArchiveRepair.RunAsync(Input, Output, new StringWriter(), Ct);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(Output));
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task Existing_output_is_not_overwritten()
    {
        await WriteOrphanAsync();
        var target = Path.Combine(Output, OrphanName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, [42], Ct);

        var exitCode = await ArchiveRepair.RunAsync(Input, Output, new StringWriter(), Ct);

        Assert.Equal(0, exitCode);
        Assert.Equal([42], await File.ReadAllBytesAsync(target, Ct));
    }

    // Reproduces what an OutOfMemoryException in ArchiveFile.FlushAsync left on the collector: an intact row group,
    // then a group closed after the payload chunk failed, and the footer written by DisposeAsync.
    private async Task<string> WriteOrphanAsync()
    {
        var path = Path.Combine(Input, OrphanName + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var schema = new ParquetSchema(ArchiveFile.ReceivedAtField, ArchiveFile.TopicField, ArchiveFile.PayloadField);
        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: Ct);

        using (var intact = writer.CreateRowGroup())
        {
            await intact.WriteAsync(ArchiveFile.ReceivedAtField, (ReadOnlyMemory<long>)_intactReceivedAt, null, null, Ct);
            await intact.WriteAsync(ArchiveFile.TopicField, _intactTopics, null);
            await intact.WriteAsync(ArchiveFile.PayloadField, _intactPayloads, null);
        }

        using (var cut = writer.CreateRowGroup())
        {
            await cut.WriteAsync(ArchiveFile.ReceivedAtField, (ReadOnlyMemory<long>)_cutReceivedAt, null, null, Ct);
            await cut.WriteAsync(ArchiveFile.TopicField, _cutTopics, null);
        }

        return path;
    }

    private static async Task<(List<(long, string?, byte[]?)> Rows, int RowGroups)> ReadAsync(string path)
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
            rows.AddRange(Enumerable.Range(0, count).Select(r => (receivedAt[r], topics[r], payloads[r])));
        }

        return (rows, reader.RowGroupCount);
    }
}

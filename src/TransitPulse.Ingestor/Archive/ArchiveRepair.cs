using Parquet;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Salvages orphaned <c>.parquet.tmp</c> archive files, run as <c>TransitPulse.Ingestor --repair &lt;input&gt; &lt;output&gt;</c>.
/// A crash during a row group flush leaves a file whose footer is valid but also lists the row group being written,
/// without all of its column chunks; every earlier row group is intact. The repair copies the intact row groups into
/// a normal archive file at the same relative path under the output folder. Input files are never changed.
/// </summary>
internal static class ArchiveRepair
{
    private const string TmpSuffix = ".tmp";

    /// <summary>Repairs every <c>*.parquet.tmp</c> under <paramref name="inputDirectory"/> and reports each file.</summary>
    /// <param name="inputDirectory">Folder with orphaned files in the archive layout (<c>feed/date/name.parquet.tmp</c>).</param>
    /// <param name="outputDirectory">Folder that receives <c>feed/date/name.parquet</c>; existing files are never overwritten.</param>
    /// <param name="log">Receives one line per file and the totals.</param>
    /// <param name="cancellationToken">Stops the repair between records.</param>
    /// <returns>Process exit code: 0 when every file was read, 1 when any file could not be repaired.</returns>
    public static async Task<int> RunAsync(string inputDirectory, string outputDirectory, TextWriter log, CancellationToken cancellationToken = default)
    {
        var rowGroupMaxBytes = new ArchiveOptions().RowGroupMaxBytes;
        int complete = 0, partial = 0, failed = 0, existing = 0;
        long rowsKept = 0, groupsDropped = 0;

        var inputs = Directory.EnumerateFiles(inputDirectory, "*.parquet" + TmpSuffix, SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var relative = Path.GetRelativePath(inputDirectory, input);
            var target = Path.Combine(outputDirectory, relative[..^TmpSuffix.Length]);
            if (File.Exists(target))
            {
                existing++;
                await log.WriteLineAsync($"EXISTS  {relative}: {target} is kept as is");
                continue;
            }

            try
            {
                var (rows, dropped) = await RepairFileAsync(input, target, FeedOf(relative), rowGroupMaxBytes, cancellationToken);
                rowsKept += rows;
                groupsDropped += dropped;
                if (dropped == 0)
                {
                    complete++;
                }
                else
                {
                    partial++;
                }

                await log.WriteLineAsync($"{(dropped == 0 ? "OK     " : "PARTIAL")} {relative}: {rows} rows kept, {dropped} row groups dropped");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                await log.WriteLineAsync($"FAILED  {relative}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await log.WriteLineAsync(
            $"Repair done: {complete} complete, {partial} partial, {failed} failed, {existing} already present; {rowsKept} rows kept, {groupsDropped} row groups dropped.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<(long Rows, int DroppedGroups)> RepairFileAsync(
        string input, string target, string feedId, long rowGroupMaxBytes, CancellationToken cancellationToken)
    {
        // The stream is ours: ParquetReader.CreateAsync(path) leaks its file handle when the footer is missing.
        await using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920, useAsync: true);
        // Throws when the footer itself is missing: nothing in the file can be located without it.
        await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
        ArchiveFile? file = null;
        long rows = 0;
        var dropped = 0;
        try
        {
            for (var i = 0; i < reader.RowGroupCount; i++)
            {
                var group = await TryReadRowGroupAsync(reader, i, cancellationToken);
                if (group is not { } columns)
                {
                    dropped++;
                    continue;
                }

                // Created on the first intact group, so a file with nothing to keep leaves no output.
                if (file is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    file = await ArchiveFile.CreateAsync(target, rowGroupMaxBytes, cancellationToken);
                }

                for (var row = 0; row < columns.ReceivedAt.Length; row++)
                {
                    var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(columns.ReceivedAt[row]);
                    await file.AddAsync(new RawRecord(feedId, receivedAt, columns.Payloads[row] ?? [], columns.Topics[row]), cancellationToken);
                }

                rows += columns.ReceivedAt.Length;
            }

            if (file is not null)
            {
                await file.CompleteAsync(cancellationToken);
                file = null;
            }
        }
        finally
        {
            // Only after a failure: the output stays a .tmp, the same "not finished" signal the writer uses.
            if (file is not null)
            {
                await file.DisposeAsync();
            }
        }

        return (rows, dropped);
    }

    // A group is kept only if all three columns read back: the footer lists the group that was being flushed even
    // when a column chunk was never written, and ColumnExists checks the file schema, not the group's chunks.
    private static async Task<(long[] ReceivedAt, string?[] Topics, byte[]?[] Payloads)?> TryReadRowGroupAsync(
        ParquetReader reader, int index, CancellationToken cancellationToken)
    {
        try
        {
            using var group = reader.OpenRowGroupReader(index);
            var count = checked((int)group.RowCount);
            var receivedAt = new long[count];
            var topics = new string?[count];
            var payloads = new byte[]?[count];
            await group.ReadAsync(ArchiveFile.ReceivedAtField, receivedAt.AsMemory(), null, cancellationToken);
            await group.ReadAsync(ArchiveFile.TopicField, topics.AsMemory(), null, cancellationToken);
            await group.ReadAsync(ArchiveFile.PayloadField, payloads.AsMemory(), null, cancellationToken);
            return (receivedAt, topics, payloads);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // The feed is not stored in the file; the archive layout puts it in the first folder.
    private static string FeedOf(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
}

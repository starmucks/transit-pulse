using Parquet;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// Writes an archive file and reads it back, run as <c>TransitPulse.Ingestor --self-test</c>.
/// Unit tests run under the JIT; this proves the archive works in the trimmed native binary itself
/// (Parquet.Net's schema code has suppressed AOT warnings), in CI and on the server after a deploy.
/// </summary>
internal static class ArchiveSelfTest
{
    /// <summary>Runs the round trip in a temporary folder and reports the result on the console.</summary>
    /// <returns>Process exit code: 0 when every record reads back unchanged, 1 otherwise.</returns>
    public static async Task<int> RunAsync()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        RawRecord[] expected =
        [
            new("self-test", at, "{\"VP\":{}}"u8.ToArray(), "/hfp/v2/journey/ongoing/vp/bus"),
            new("self-test", at.AddMilliseconds(1), [0x08, 0x96, 0x01], null),
            new("self-test", at.AddMilliseconds(2), [], "t"),
        ];

        var dir = Directory.CreateTempSubdirectory("tp-self-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "self-test.parquet");

            // A tiny row group limit forces several row groups, as a busy hour would.
            var file = await ArchiveFile.CreateAsync(path, rowGroupMaxBytes: 4, CancellationToken.None);
            foreach (var record in expected)
            {
                await file.AddAsync(record, CancellationToken.None);
            }

            await file.CompleteAsync(CancellationToken.None);

            var (actual, rowGroups) = await ReadAsync(path);
            var matches = actual.Count == expected.Length && rowGroups > 1 && expected.Select((e, i) =>
                e.ReceivedAt.ToUnixTimeMilliseconds() == actual[i].ReceivedAt
                && e.Topic == actual[i].Topic
                && e.Payload.AsSpan().SequenceEqual(actual[i].Payload)).All(ok => ok);

            if (!matches)
            {
                await Console.Error.WriteLineAsync($"Archive self-test FAILED: {actual.Count} records in {rowGroups} row groups do not match what was written.");
                return 1;
            }

            Console.WriteLine($"Archive self-test passed: {actual.Count} records in {rowGroups} row groups.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Archive self-test FAILED: {ex}");
            return 1;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<(List<(long ReceivedAt, string? Topic, byte[] Payload)> Rows, int RowGroups)> ReadAsync(string path)
    {
        var rows = new List<(long, string?, byte[])>();
        await using var reader = await ParquetReader.CreateAsync(path);
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            var count = (int)group.RowCount;
            var receivedAt = new long[count];
            var topics = new string?[count];
            var payloads = new byte[]?[count];
            await group.ReadAsync(ArchiveFile.ReceivedAtField, receivedAt.AsMemory(), null);
            await group.ReadAsync(ArchiveFile.TopicField, topics.AsMemory(), null);
            await group.ReadAsync(ArchiveFile.PayloadField, payloads.AsMemory(), null);
            for (var row = 0; row < count; row++)
            {
                rows.Add((receivedAt[row], topics[row], payloads[row] ?? []));
            }
        }

        return (rows, reader.RowGroupCount);
    }
}

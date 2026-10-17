using Parquet;
using Parquet.Schema;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Archive;

/// <summary>
/// One Parquet + zstd archive file of raw records. It is written as <c>&lt;path&gt;.tmp</c> and renamed to its
/// final path only after the footer is written, so readers and sync scripts never see an incomplete file.
/// Uses the column API only: the reflection-based <c>ParquetSerializer</c> is not Native AOT safe.
/// </summary>
internal sealed class ArchiveFile : IAsyncDisposable
{
    internal static readonly DataField<long> ReceivedAtField = new("received_at_unix_ms");
    internal static readonly DataField<string> TopicField = new("topic", true);
    internal static readonly DataField<byte[]> PayloadField = new("payload");

    private static readonly ParquetSchema _schema = new(ReceivedAtField, TopicField, PayloadField);

    private readonly string _tmpPath;
    private readonly FileStream _stream;
    private readonly ParquetWriter _writer;
    private readonly long _rowGroupMaxBytes;
    private readonly List<long> _receivedAt = [];
    private readonly List<string?> _topics = [];
    private readonly List<byte[]> _payloads = [];
    private long _bufferedBytes;
    private bool _disposed;

    private ArchiveFile(string path, string tmpPath, FileStream stream, ParquetWriter writer, long rowGroupMaxBytes)
    {
        Path = path;
        _tmpPath = tmpPath;
        _stream = stream;
        _writer = writer;
        _rowGroupMaxBytes = rowGroupMaxBytes;
    }

    /// <summary>Final path the file gets after <see cref="CompleteAsync"/>.</summary>
    public string Path { get; }

    /// <summary>Records added so far.</summary>
    public long RecordCount { get; private set; }

    /// <summary>Starts a new file; fails if a file with the same temporary name already exists.</summary>
    /// <param name="path">Final path of the <c>.parquet</c> file.</param>
    /// <param name="rowGroupMaxBytes">Buffered payload size that triggers writing a row group; bounds memory use.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    public static async Task<ArchiveFile> CreateAsync(string path, long rowGroupMaxBytes, CancellationToken cancellationToken)
    {
        var tmpPath = path + ".tmp";
        var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 81_920, useAsync: true);
        try
        {
            var options = new ParquetOptions { CompressionMethod = CompressionMethod.Zstd };
            var writer = await ParquetWriter.CreateAsync(_schema, stream, options, cancellationToken: cancellationToken);
            return new ArchiveFile(path, tmpPath, stream, writer, rowGroupMaxBytes);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Buffers a record and writes a row group once the buffer is large enough.</summary>
    /// <param name="record">Record to append.</param>
    /// <param name="cancellationToken">Cancels a row group write.</param>
    public async ValueTask AddAsync(RawRecord record, CancellationToken cancellationToken)
    {
        _receivedAt.Add(record.ReceivedAt.ToUnixTimeMilliseconds());
        _topics.Add(record.Topic);
        _payloads.Add(record.Payload);
        _bufferedBytes += record.Payload.Length;
        RecordCount++;

        if (_bufferedBytes >= _rowGroupMaxBytes)
        {
            await FlushAsync(cancellationToken);
        }
    }

    /// <summary>Writes the remaining records and the footer, then renames the file to <see cref="Path"/>.</summary>
    /// <param name="cancellationToken">Cancels the final row group write.</param>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(cancellationToken);
        }
        finally
        {
            await DisposeAsync();
        }

        File.Move(_tmpPath, Path);
    }

    /// <summary>
    /// Closes the file without renaming it: an abandoned file stays as <c>.tmp</c> and is never picked up by sync.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _writer.DisposeAsync();
        await _stream.DisposeAsync();
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_payloads.Count == 0)
        {
            return;
        }

        using (var group = _writer.CreateRowGroup())
        {
            await group.WriteAsync(ReceivedAtField, (ReadOnlyMemory<long>)_receivedAt.ToArray(), null, null, cancellationToken);
            await group.WriteAsync(TopicField, _topics, null);
            await group.WriteAsync(PayloadField, _payloads, null);
        }

        _receivedAt.Clear();
        _topics.Clear();
        _payloads.Clear();
        _bufferedBytes = 0;
    }
}

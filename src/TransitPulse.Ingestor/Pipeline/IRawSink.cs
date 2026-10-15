namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Destination for raw records, such as the hourly archive files. The pipeline calls it from a single
/// reader, so implementations need no locking and see records in arrival order.
/// </summary>
public interface IRawSink
{
    /// <summary>Accepts one record; may buffer it.</summary>
    /// <param name="record">The record to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the record is accepted.</returns>
    ValueTask WriteAsync(RawRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Flushes buffered records and closes open files. Called once, after the last <see cref="WriteAsync"/> on shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>A task that completes when everything is durable.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

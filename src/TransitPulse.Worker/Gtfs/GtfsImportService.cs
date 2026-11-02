using Microsoft.Extensions.Options;
using Npgsql;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Worker.Gtfs;

/// <summary>
/// Keeps each feed's schedule in Postgres current: applies the schema migrations on start, then imports the newest
/// complete snapshot of every feed at start and once per <see cref="GtfsImportOptions.Interval"/>.
/// </summary>
internal sealed partial class GtfsImportService(
    NpgsqlDataSource dataSource,
    GtfsImporter importer,
    IOptions<GtfsImportOptions> options,
    TimeProvider timeProvider,
    ILogger<GtfsImportService> logger) : BackgroundService
{
    // Last rejected file per feed: a broken export is read once, not on every pass until the next export arrives.
    private readonly Dictionary<string, string> _rejected = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not caught: without the schema nothing can be imported, so a failure here stops the host.
        var applied = await SchemaMigrator.MigrateAsync(dataSource, stoppingToken);
        LogMigrated(applied.Count == 0 ? "none" : string.Join(", ", applied));

        using var timer = new PeriodicTimer(options.Value.Interval, timeProvider);
        do
        {
            await ImportAllAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: imports the newest complete snapshot of every feed; failures are logged, never thrown.</summary>
    /// <param name="cancellationToken">Stops the pass at the next feed or database call.</param>
    internal async Task ImportAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GtfsSnapshotFile> snapshots;
        try
        {
            snapshots = GtfsSnapshotDirectory.Latest(options.Value.SnapshotDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk gone or a folder without access must not stop the host; the next pass looks again.
            LogListingFailed(ex, Path.GetFullPath(options.Value.SnapshotDirectory));
            return;
        }

        if (snapshots.Count == 0)
        {
            LogNoSnapshots(Path.GetFullPath(options.Value.SnapshotDirectory));
        }

        foreach (var snapshot in snapshots)
        {
            var file = Path.GetFileName(snapshot.Path);
            if (_rejected.GetValueOrDefault(snapshot.FeedId) == snapshot.Path)
            {
                continue;
            }

            var started = timeProvider.GetTimestamp();
            try
            {
                var result = await importer.ImportAsync(snapshot.FeedId, snapshot.Path, cancellationToken);
                var elapsed = timeProvider.GetElapsedTime(started);
                switch (result.Outcome)
                {
                    case GtfsImportOutcome.Imported:
                        var stopTimes = result.Report!.Rows.GetValueOrDefault("stop_times.txt");
                        LogImported(snapshot.FeedId, file, result.SnapshotId, stopTimes, elapsed);
                        break;
                    case GtfsImportOutcome.Unchanged:
                        LogUnchanged(snapshot.FeedId, file, result.SnapshotId);
                        break;
                    case GtfsImportOutcome.Reactivated:
                        LogReactivated(snapshot.FeedId, file, result.SnapshotId);
                        break;
                    case GtfsImportOutcome.Rejected:
                        _rejected[snapshot.FeedId] = snapshot.Path;
                        LogRejected(snapshot.FeedId, file, string.Join("; ", result.Report!.Problems));
                        break;
                }

                if (result.CleanupFailure is { } cleanupFailure)
                {
                    LogCleanupFailed(cleanupFailure, snapshot.FeedId);
                }
                else if (result.StaleSnapshots > 0)
                {
                    LogStale(snapshot.FeedId, result.StaleSnapshots);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One feed failing (database away, unreadable zip) must not stop the others; the next pass retries it.
                LogFailed(ex, snapshot.FeedId, file);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema migrations applied: {Migrations}")]
    private partial void LogMigrated(string migrations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No complete GTFS snapshot under {Directory}")]
    private partial void LogNoSnapshots(string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "GTFS snapshots under {Directory} could not be listed; the next pass retries")]
    private partial void LogListingFailed(Exception exception, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "GTFS {FeedId}: {File} is an old version still kept; snapshot {SnapshotId} is active again")]
    private partial void LogReactivated(string feedId, string file, int? snapshotId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GTFS {FeedId}: dropping old snapshots failed; the next import retries")]
    private partial void LogCleanupFailed(Exception exception, string feedId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GTFS {FeedId}: {File} imported as snapshot {SnapshotId}, {StopTimes} stop times in {Elapsed}")]
    private partial void LogImported(string feedId, string file, int? snapshotId, long stopTimes, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Information, Message = "GTFS {FeedId}: {File} is already active as snapshot {SnapshotId}")]
    private partial void LogUnchanged(string feedId, string file, int? snapshotId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GTFS {FeedId}: {File} rejected, the previous schedule stays active: {Problems}")]
    private partial void LogRejected(string feedId, string file, string problems);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GTFS {FeedId}: {Count} old snapshot(s) not dropped, readers held the tables; the next import retries")]
    private partial void LogStale(string feedId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "GTFS {FeedId}: import of {File} failed; the next pass retries")]
    private partial void LogFailed(Exception exception, string feedId, string file);
}

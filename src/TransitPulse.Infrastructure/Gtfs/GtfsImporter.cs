using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// Makes a GTFS snapshot zip the active schedule of its feed. The snapshot is copied into new partitions and checked with
/// <see cref="GtfsSnapshotCheck"/> in the same pass, then activated in that one transaction: readers see the old schedule
/// or the new one, never a mix, and a rejected or failed import leaves nothing behind. Old versions are dropped afterwards,
/// once no reader holds the tables.
/// </summary>
/// <remarks>
/// "Never a mix" needs the reader to keep the version it chose: in one transaction it locks the parent tables it reads
/// IN ACCESS SHARE MODE before choosing the active <c>snapshot_id</c>, or it chooses and reads in one statement. Otherwise
/// a newer import can drop the chosen version's partitions in between and the read quietly returns nothing; REPEATABLE READ
/// alone does not prevent that, and a <c>snapshot_id</c> kept between reads goes stale the same way.
/// </remarks>
/// <param name="dataSource">Database with the <c>gtfs</c> schema.</param>
/// <param name="cleanupRetryDelay">
/// Pause between attempts to drop old versions while readers hold the tables (default 2 s); after
/// <see cref="CleanupAttempts"/> attempts the drop is left to the next import.
/// </param>
public sealed class GtfsImporter(NpgsqlDataSource dataSource, TimeSpan? cleanupRetryDelay = null)
{
    /// <summary>How many times one import tries to drop old versions.</summary>
    public const int CleanupAttempts = 5;

    // Primary keys as in 0001_gtfs_schema.sql: built after the copy, then adopted by ATTACH. A key that differs from the
    // parent's makes ATTACH fail ("multiple primary keys"); the importer tests also count one index per partition.
    private static readonly (string Name, string Key)[] _tables =
    [
        ("routes", "snapshot_id, route_id"), ("stops", "snapshot_id, stop_id"), ("calendar", "snapshot_id, service_id"),
        ("calendar_dates", "snapshot_id, service_id, date"), ("shapes", "snapshot_id, shape_id, sequence"),
        ("trips", "snapshot_id, trip_id"), ("stop_times", "snapshot_id, trip_id, stop_sequence"),
    ];

    private readonly TimeSpan _cleanupRetryDelay = cleanupRetryDelay is { } delay && delay < TimeSpan.Zero
        ? throw new ArgumentOutOfRangeException(nameof(cleanupRetryDelay), delay, "The delay cannot be negative.")
        : cleanupRetryDelay ?? TimeSpan.FromSeconds(2);

    /// <summary>Imports one snapshot unless it is already the feed's active one.</summary>
    /// <param name="feedId">Feed the snapshot belongs to, e.g. <c>hsl</c>.</param>
    /// <param name="zipPath">The snapshot zip.</param>
    /// <param name="cancellationToken">Stops the import; the transaction rolls back.</param>
    /// <returns>What happened, the active snapshot id unless rejected, and what is left of the old versions.</returns>
    public async Task<GtfsImportResult> ImportAsync(string feedId, string zipPath, CancellationToken cancellationToken)
    {
        byte[] sha256;
        await using (var file = File.OpenRead(zipPath))
        {
            sha256 = await SHA256.HashDataAsync(file, cancellationToken);
        }

        var (outcome, id, report) = await LoadAsync(feedId, zipPath, sha256, cancellationToken);
        try
        {
            return new(outcome, id, report, await DropInactiveAsync(feedId, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The new version is committed by now: a failed cleanup is reported, not turned into a failed import.
            return new(outcome, id, report, 0, ex);
        }
    }

    private async Task<(GtfsImportOutcome, int?, GtfsSnapshotReport?)> LoadAsync(
        string feedId, string zipPath, byte[] sha256, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFeedAsync(connection, feedId, cancellationToken);
        if (await WithHashAsync(connection, feedId, sha256, cancellationToken) is var (known, isActive))
        {
            if (isActive)
            {
                return (GtfsImportOutcome.Unchanged, known, null);
            }

            // An old version readers kept from being dropped: its partitions are attached, so switch back to it.
            await ActivateAsync(connection, feedId, known, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (GtfsImportOutcome.Reactivated, known, null);
        }

        using var feed = GtfsFeed.Open(zipPath);
        var id = await CreateSnapshotAsync(connection, feedId, Path.GetFileName(zipPath), sha256, cancellationToken);
        var check = new GtfsSnapshotCheck();
        try
        {
            await CopyAllAsync(new Copier(connection, id, cancellationToken), feed, check);
        }
        catch (GtfsFormatException ex)
        {
            check.Abort(ex);
        }

        var report = check.Finish();
        if (!report.IsValid)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (GtfsImportOutcome.Rejected, null, report);
        }

        if (await AddKeysAsync(connection, id, cancellationToken) is { } duplicate)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (GtfsImportOutcome.Rejected, null, report with { Problems = [.. report.Problems, duplicate] });
        }

        await AttachAsync(connection, id, cancellationToken);
        await ActivateAsync(connection, feedId, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (GtfsImportOutcome.Imported, id, report);
    }

    // Returns how many old versions are left: 0, unless readers held the tables on every attempt.
    private async Task<int> DropInactiveAsync(string feedId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var left = await TryDropInactiveAsync(dataSource, feedId, cancellationToken);
            if (left == 0 || attempt == CleanupAttempts)
            {
                return left;
            }

            await Task.Delay(_cleanupRetryDelay, cancellationToken);
        }
    }

    private static async Task<int> TryDropInactiveAsync(NpgsqlDataSource dataSource, string feedId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFeedAsync(connection, feedId, cancellationToken);
        var ids = new List<int>();
        await using (var select = new NpgsqlCommand("SELECT snapshot_id FROM gtfs.snapshot WHERE feed_id = $1 AND NOT is_active", connection))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = feedId });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt32(0));
            }
        }

        if (ids.Count == 0)
        {
            return 0;
        }

        try
        {
            // DROP of a partition needs ACCESS EXCLUSIVE on its parent. NOWAIT takes all seven at once or fails at once:
            // waiting would queue every new reader behind it, and taking them one by one could deadlock with a reader.
            await ExecuteAsync(connection,
                $"LOCK TABLE {string.Join(", ", _tables.Select(table => "gtfs." + table.Name))} IN ACCESS EXCLUSIVE MODE NOWAIT",
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return ids.Count;
        }

        foreach (var id in ids)
        {
            await ExecuteAsync(connection, ForEachTable(id, "DROP TABLE gtfs.{0}_{1}"), cancellationToken);
        }

        await ExecuteAsync(connection, "DELETE FROM gtfs.snapshot WHERE snapshot_id = ANY($1)", cancellationToken, ids.ToArray());
        await transaction.CommitAsync(cancellationToken);
        return 0;
    }

    // Imports and cleanups of one feed take turns; a transaction-level lock ends with the transaction, pool or not.
    private static Task LockFeedAsync(NpgsqlConnection connection, string feedId, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtext($1))", cancellationToken, feedId);

    // Two statements: a unique index is checked row by row, so flipping both rows in one UPDATE could collide.
    private static async Task ActivateAsync(NpgsqlConnection connection, string feedId, int id, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "UPDATE gtfs.snapshot SET is_active = false WHERE feed_id = $1 AND is_active", cancellationToken, feedId);
        await ExecuteAsync(connection, "UPDATE gtfs.snapshot SET is_active = true WHERE snapshot_id = $1", cancellationToken, id);
    }

    // Reference order, as in GtfsSnapshotCheck.Run: the check needs routes, stops, calendars and shapes before trips.
    private static async Task CopyAllAsync(Copier copy, GtfsFeed feed, GtfsSnapshotCheck check)
    {
        await copy.TableAsync("routes", "route_id, short_name, long_name, route_type", feed.Routes(), check.Add, async (w, r, ct) =>
        {
            await w.WriteAsync(r.RouteId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(r.ShortName, NpgsqlDbType.Text, ct);
            await w.WriteAsync(r.LongName, NpgsqlDbType.Text, ct);
            await w.WriteAsync(r.RouteType, NpgsqlDbType.Integer, ct);
        });
        await copy.TableAsync("stops", "stop_id, name, lat, lon, location_type, parent_station", feed.Stops(), check.Add, async (w, s, ct) =>
        {
            await w.WriteAsync(s.StopId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(s.Name, NpgsqlDbType.Text, ct);
            await w.WriteAsync(s.Lat, NpgsqlDbType.Double, ct);
            await w.WriteAsync(s.Lon, NpgsqlDbType.Double, ct);
            await w.WriteAsync((short)s.LocationType, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync(s.ParentStation, NpgsqlDbType.Text, ct);
        });

        var hasCalendar = feed.Contains("calendar.txt");
        var hasDates = feed.Contains("calendar_dates.txt");
        if (!hasCalendar && !hasDates)
        {
            check.Problem("neither calendar.txt nor calendar_dates.txt is present", "");
        }

        if (hasCalendar)
        {
            await copy.TableAsync("calendar", "service_id, days, start_date, end_date", feed.Calendars(), check.Add, async (w, c, ct) =>
            {
                await w.WriteAsync(c.ServiceId, NpgsqlDbType.Text, ct);
                await w.WriteAsync((short)c.Days, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(c.Start, NpgsqlDbType.Date, ct);
                await w.WriteAsync(c.End, NpgsqlDbType.Date, ct);
            });
        }

        if (hasDates)
        {
            await copy.TableAsync("calendar_dates", "service_id, date, added", feed.CalendarDates(), check.Add, async (w, d, ct) =>
            {
                await w.WriteAsync(d.ServiceId, NpgsqlDbType.Text, ct);
                await w.WriteAsync(d.Date, NpgsqlDbType.Date, ct);
                await w.WriteAsync(d.Added, NpgsqlDbType.Boolean, ct);
            });
        }

        await copy.TableAsync("shapes", "shape_id, sequence, lat, lon, dist_traveled", feed.ShapePoints(), check.Add, async (w, p, ct) =>
        {
            await w.WriteAsync(p.ShapeId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(p.Sequence, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(p.Lat, NpgsqlDbType.Double, ct);
            await w.WriteAsync(p.Lon, NpgsqlDbType.Double, ct);
            await w.WriteAsync(p.DistTraveled, NpgsqlDbType.Double, ct);
        });
        await copy.TableAsync("trips", "trip_id, route_id, service_id, direction_id, shape_id, headsign", feed.Trips(), check.Add, async (w, t, ct) =>
        {
            await w.WriteAsync(t.TripId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(t.RouteId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(t.ServiceId, NpgsqlDbType.Text, ct);
            await w.WriteAsync((short?)t.DirectionId, NpgsqlDbType.Smallint, ct);
            await w.WriteAsync(t.ShapeId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(t.Headsign, NpgsqlDbType.Text, ct);
        });
        await copy.TableAsync(
            "stop_times", "trip_id, stop_sequence, stop_id, arrival_sec, departure_sec, shape_dist_traveled", feed.StopTimes(), check.Add,
            async (w, s, ct) =>
            {
                await w.WriteAsync(s.TripId, NpgsqlDbType.Text, ct);
                await w.WriteAsync(s.StopSequence, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(s.StopId, NpgsqlDbType.Text, ct);
                await w.WriteAsync(Seconds(s.Arrival), NpgsqlDbType.Integer, ct);
                await w.WriteAsync(Seconds(s.Departure), NpgsqlDbType.Integer, ct);
                await w.WriteAsync(s.ShapeDistTraveled, NpgsqlDbType.Double, ct);
            });
    }

    private static int? Seconds(TimeSpan? time) => time is { } t ? (int)t.TotalSeconds : null;

    private static async Task<int> CreateSnapshotAsync(
        NpgsqlConnection connection, string feedId, string fileName, byte[] sha256, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO gtfs.snapshot (feed_id, file_name, sha256) VALUES ($1, $2, $3) RETURNING snapshot_id", connection);
        insert.Parameters.Add(new NpgsqlParameter { Value = feedId });
        insert.Parameters.Add(new NpgsqlParameter { Value = fileName });
        insert.Parameters.Add(new NpgsqlParameter { Value = sha256 });
        var id = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;

        // Plain tables, attached as partitions only after the copy: CREATE TABLE ... PARTITION OF would hold ACCESS
        // EXCLUSIVE on the parent, blocking every reader, for the whole import. The CHECK spares ATTACH a table scan.
        // No index yet: one sorted build after the copy is faster than millions of random B-tree inserts.
        await ExecuteAsync(connection, ForEachTable(id,
            "CREATE TABLE gtfs.{0}_{1} (LIKE gtfs.{0} INCLUDING ALL EXCLUDING INDEXES, CHECK (snapshot_id = {1}))"), cancellationToken);
        return id;
    }

    // Returns the first duplicate key found as a problem; the transaction is then aborted and must roll back.
    private static async Task<string?> AddKeysAsync(NpgsqlConnection connection, int id, CancellationToken cancellationToken)
    {
        foreach (var table in _tables)
        {
            try
            {
                await ExecuteAsync(connection, string.Create(CultureInfo.InvariantCulture,
                    $"ALTER TABLE gtfs.{table.Name}_{id} ADD PRIMARY KEY ({table.Key})"), cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // The check does not look for duplicate keys such as a repeated stop_sequence; the primary key does.
                // Not ex.Detail: without Include Error Detail, Npgsql puts a "Detail redacted" notice there, not the key.
                return $"{table.Name}.txt: duplicate key ({table.Key})";
            }
        }

        return null;
    }

    // ATTACH takes only SHARE UPDATE EXCLUSIVE on the parent, which readers do not conflict with.
    private static Task AttachAsync(NpgsqlConnection connection, int id, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, ForEachTable(id, "ALTER TABLE gtfs.{0} ATTACH PARTITION gtfs.{0}_{1} FOR VALUES IN ({1})"), cancellationToken);

    // The feed's snapshot of this file, active or an old version not dropped yet; null when the file is new.
    private static async Task<(int Id, bool IsActive)?> WithHashAsync(
        NpgsqlConnection connection, string feedId, byte[] sha256, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT snapshot_id, is_active FROM gtfs.snapshot WHERE feed_id = $1 AND sha256 = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = feedId });
        command.Parameters.Add(new NpgsqlParameter { Value = sha256 });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt32(0), reader.GetBoolean(1)) : null;
    }

    // One statement per table; table names are constants and the id an integer, never input.
    private static string ForEachTable(int id, string format) =>
        string.Join(";", _tables.Select(table => string.Format(CultureInfo.InvariantCulture, format, table.Name, id)));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
        // No 30 s default: the primary key of ~10 million HSL stop_times takes longer to build, and the feed lock may wait
        // for a running import. The import stops through the cancellation token. (COPY is not affected: its timeout
        // applies to each network read or write, not to the whole copy.)
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class Copier(NpgsqlConnection connection, int snapshotId, CancellationToken cancellationToken)
    {
        // Into the snapshot's own table, not the parent: no per-row routing by partition key.
        public async Task TableAsync<T>(
            string table, string columns, IEnumerable<T> rows, Action<T> check, Func<NpgsqlBinaryImporter, T, CancellationToken, Task> write)
        {
            var sql = string.Create(CultureInfo.InvariantCulture, $"COPY gtfs.{table}_{snapshotId} (snapshot_id, {columns}) FROM STDIN (FORMAT BINARY)");
            await using var writer = await connection.BeginBinaryImportAsync(sql, cancellationToken);
            foreach (var row in rows)
            {
                check(row);
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(snapshotId, NpgsqlDbType.Integer, cancellationToken);
                await write(writer, row, cancellationToken);
            }

            // Without Complete the server discards the rows: an exception above never leaves half a table.
            await writer.CompleteAsync(cancellationToken);
        }
    }
}

/// <summary>Outcome of <see cref="GtfsImporter.ImportAsync"/>.</summary>
/// <param name="Outcome">What happened to the snapshot.</param>
/// <param name="SnapshotId">The feed's active snapshot after the call; <see langword="null"/> when this one was rejected.</param>
/// <param name="Report">What the check found; <see langword="null"/> when the file was known and not read.</param>
/// <param name="StaleSnapshots">Old versions readers kept from being dropped; the next import retries.</param>
/// <param name="CleanupFailure">
/// Why dropping old versions failed after the outcome was committed; <paramref name="StaleSnapshots"/> is then unknown.
/// </param>
public sealed record GtfsImportResult(
    GtfsImportOutcome Outcome, int? SnapshotId, GtfsSnapshotReport? Report, int StaleSnapshots, Exception? CleanupFailure = null);

/// <summary>What <see cref="GtfsImporter.ImportAsync"/> did with a snapshot.</summary>
public enum GtfsImportOutcome
{
    /// <summary>Loaded and made the feed's active schedule.</summary>
    Imported,

    /// <summary>The same file is already active; nothing was read.</summary>
    Unchanged,

    /// <summary>The file is an old version not dropped yet; it became active again without being read.</summary>
    Reactivated,

    /// <summary>Failed the check; the previous schedule stays active.</summary>
    Rejected,
}

using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// Makes a GTFS snapshot zip the active schedule of its feed. The snapshot is copied into new partitions and checked with
/// <see cref="GtfsSnapshotCheck"/> in the same pass, then activated in that one transaction: readers see the old schedule
/// or the new one, never a mix, and a rejected or failed import leaves nothing behind. Old versions are dropped afterwards.
/// </summary>
/// <param name="dataSource">Database with the <c>gtfs</c> schema.</param>
/// <param name="cleanupLockTimeout">
/// How long dropping an old version may wait for readers (default 10 s); past it the drop is left to the next import.
/// </param>
public sealed class GtfsImporter(NpgsqlDataSource dataSource, TimeSpan? cleanupLockTimeout = null)
{
    // Primary keys as in 0001_gtfs_schema.sql: built after the copy, then adopted by ATTACH. A key that differs from the
    // parent's would not fail, ATTACH would build a second index; the importer tests count one index per partition.
    private static readonly (string Name, string Key)[] _tables =
    [
        ("routes", "snapshot_id, route_id"), ("stops", "snapshot_id, stop_id"), ("calendar", "snapshot_id, service_id"),
        ("calendar_dates", "snapshot_id, service_id, date"), ("shapes", "snapshot_id, shape_id, sequence"),
        ("trips", "snapshot_id, trip_id"), ("stop_times", "snapshot_id, trip_id, stop_sequence"),
    ];

    private readonly TimeSpan _cleanupLockTimeout = cleanupLockTimeout ?? TimeSpan.FromSeconds(10);

    /// <summary>Imports one snapshot unless it is already the feed's active one.</summary>
    /// <param name="feedId">Feed the snapshot belongs to, e.g. <c>hsl</c>.</param>
    /// <param name="zipPath">The snapshot zip.</param>
    /// <param name="cancellationToken">Stops the import; the transaction rolls back.</param>
    /// <returns>What happened, the active snapshot id unless rejected, and how many old versions still wait to be dropped.</returns>
    public async Task<GtfsImportResult> ImportAsync(string feedId, string zipPath, CancellationToken cancellationToken)
    {
        byte[] sha256;
        await using (var file = File.OpenRead(zipPath))
        {
            sha256 = await SHA256.HashDataAsync(file, cancellationToken);
        }

        // Leftovers of an earlier blocked cleanup go first: one of them may hold this very file's hash.
        await DropInactiveAsync(feedId, cancellationToken);
        var (outcome, id, report) = await LoadAsync(feedId, zipPath, sha256, cancellationToken);
        var stale = await DropInactiveAsync(feedId, cancellationToken);
        return new(outcome, id, report, stale);
    }

    private async Task<(GtfsImportOutcome, int?, GtfsSnapshotReport?)> LoadAsync(
        string feedId, string zipPath, byte[] sha256, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Imports of one feed take turns; a transaction-level lock ends with the transaction, pool or not.
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtext($1))", cancellationToken, feedId);
        if (await ActiveWithHashAsync(connection, feedId, sha256, cancellationToken) is { } active)
        {
            return (GtfsImportOutcome.Unchanged, active, null);
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
        // Two statements: a unique index is checked row by row, so flipping both rows in one UPDATE could collide.
        await ExecuteAsync(connection, "UPDATE gtfs.snapshot SET is_active = false WHERE feed_id = $1 AND is_active", cancellationToken, feedId);
        await ExecuteAsync(connection, "UPDATE gtfs.snapshot SET is_active = true WHERE snapshot_id = $1", cancellationToken, id);
        await transaction.CommitAsync(cancellationToken);
        return (GtfsImportOutcome.Imported, id, report);
    }

    // Returns how many inactive snapshots are left: 0, unless a reader held a table past the lock timeout.
    private async Task<int> DropInactiveAsync(string feedId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // DROP of a partition takes ACCESS EXCLUSIVE on the parent, and new readers queue behind a waiting DROP.
            var timeout = string.Create(CultureInfo.InvariantCulture, $"{(long)_cleanupLockTimeout.TotalMilliseconds}ms");
            await ExecuteAsync(connection, "SELECT set_config('lock_timeout', $1, true)", cancellationToken, timeout);
            await using var delete = new NpgsqlCommand("DELETE FROM gtfs.snapshot WHERE feed_id = $1 AND NOT is_active RETURNING snapshot_id", connection);
            delete.Parameters.Add(new NpgsqlParameter { Value = feedId });
            var ids = new List<int>();
            await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(reader.GetInt32(0));
                }
            }

            foreach (var id in ids)
            {
                await ExecuteAsync(connection, ForEachTable(id, "DROP TABLE IF EXISTS gtfs.{0}_{1}"), cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM gtfs.snapshot WHERE feed_id = $1 AND NOT is_active", connection);
            count.Parameters.Add(new NpgsqlParameter { Value = feedId });
            return (int)(long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
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
                // Detail (the key values) is redacted unless the connection string sets Include Error Detail.
                return $"{table.Name}.txt: duplicate key: {ex.Detail ?? ex.ConstraintName}";
            }
        }

        return null;
    }

    // ATTACH takes only SHARE UPDATE EXCLUSIVE on the parent, which readers do not conflict with.
    private static Task AttachAsync(NpgsqlConnection connection, int id, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, ForEachTable(id, "ALTER TABLE gtfs.{0} ATTACH PARTITION gtfs.{0}_{1} FOR VALUES IN ({1})"), cancellationToken);

    private static async Task<int?> ActiveWithHashAsync(NpgsqlConnection connection, string feedId, byte[] sha256, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT snapshot_id FROM gtfs.snapshot WHERE feed_id = $1 AND sha256 = $2 AND is_active", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = feedId });
        command.Parameters.Add(new NpgsqlParameter { Value = sha256 });
        return await command.ExecuteScalarAsync(cancellationToken) as int?;
    }

    // One statement per table; table names are constants and the id an integer, never input.
    private static string ForEachTable(int id, string format) =>
        string.Join(";", _tables.Select(table => string.Format(CultureInfo.InvariantCulture, format, table.Name, id)));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
        // No 30 s default: the primary key of ~10 million HSL stop_times takes longer to build. The import stops through
        // the cancellation token, and waits for readers are bounded by lock_timeout.
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
/// <param name="Report">What the check found; <see langword="null"/> when the file was already active and not read.</param>
/// <param name="StaleSnapshots">Old versions a reader kept from being dropped; the next import retries.</param>
public sealed record GtfsImportResult(GtfsImportOutcome Outcome, int? SnapshotId, GtfsSnapshotReport? Report, int StaleSnapshots);

/// <summary>What <see cref="GtfsImporter.ImportAsync"/> did with a snapshot.</summary>
public enum GtfsImportOutcome
{
    /// <summary>Loaded and made the feed's active schedule.</summary>
    Imported,

    /// <summary>The same file is already active; nothing was read.</summary>
    Unchanged,

    /// <summary>Failed the check; the previous schedule stays active.</summary>
    Rejected,
}

using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// Loads a GTFS snapshot zip into the <c>gtfs</c> schema as a new, inactive snapshot with its own partitions, checking
/// it with <see cref="GtfsSnapshotCheck"/> in the same pass. Everything runs in one transaction: a snapshot that fails
/// the check, or an import that fails midway, leaves no rows and no partitions behind.
/// </summary>
public static class GtfsImporter
{
    private static readonly string[] _tables = ["routes", "stops", "calendar", "calendar_dates", "shapes", "trips", "stop_times"];

    /// <summary>Imports one snapshot.</summary>
    /// <param name="dataSource">Database with the <c>gtfs</c> schema.</param>
    /// <param name="feedId">Feed the snapshot belongs to, e.g. <c>hsl</c>.</param>
    /// <param name="zipPath">The snapshot zip.</param>
    /// <param name="cancellationToken">Stops the import; the transaction rolls back.</param>
    /// <returns>The new snapshot id, or <see langword="null"/> when the check rejected the snapshot; the check report either way.</returns>
    public static async Task<GtfsImportResult> ImportAsync(
        NpgsqlDataSource dataSource, string feedId, string zipPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        byte[] sha256;
        await using (var file = File.OpenRead(zipPath))
        {
            sha256 = await SHA256.HashDataAsync(file, cancellationToken);
        }

        using var feed = GtfsFeed.Open(zipPath);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
            return new(null, report);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(id, report);
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

        // Partition names come from an integer id, never from input.
        var partitions = string.Join(";", _tables.Select(table =>
            string.Create(CultureInfo.InvariantCulture, $"CREATE TABLE gtfs.{table}_{id} PARTITION OF gtfs.{table} FOR VALUES IN ({id})")));
        await using var create = new NpgsqlCommand(partitions, connection);
        await create.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private sealed class Copier(NpgsqlConnection connection, int snapshotId, CancellationToken cancellationToken)
    {
        // Straight into the snapshot's partition: COPY into the parent would route every row through the partition key.
        public async Task TableAsync<T>(
            string table, string columns, IEnumerable<T> rows, Action<T> check, Func<NpgsqlBinaryImporter, T, CancellationToken, Task> write)
        {
            var sql = string.Create(CultureInfo.InvariantCulture, $"COPY gtfs.{table}_{snapshotId} (snapshot_id, {columns}) FROM STDIN (FORMAT BINARY)");
            try
            {
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
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // The check does not look for duplicate keys such as a repeated stop_sequence; the primary key does.
                // Detail (the key values) is redacted unless the connection string sets Include Error Detail.
                throw new GtfsFormatException($"{table}.txt", $"duplicate key: {ex.Detail ?? ex.ConstraintName}");
            }
        }
    }
}

/// <summary>Outcome of <see cref="GtfsImporter.ImportAsync"/>.</summary>
/// <param name="SnapshotId">Id of the imported, still inactive snapshot; <see langword="null"/> when it was rejected.</param>
/// <param name="Report">What the check found.</param>
public sealed record GtfsImportResult(int? SnapshotId, GtfsSnapshotReport Report);

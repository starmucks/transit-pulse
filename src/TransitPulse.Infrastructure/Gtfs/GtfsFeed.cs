using System.IO.Compression;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>
/// A GTFS snapshot read straight from its zip: each method streams one file, decompressing as it goes, so even the
/// ~1 GB <c>stop_times.txt</c> of HSL is never held in memory or extracted to disk.
/// </summary>
/// <remarks>Enumerate one file at a time: all files share the archive's single stream.</remarks>
/// <param name="zip">The snapshot archive; disposed with the feed.</param>
public sealed class GtfsFeed(ZipArchive zip) : IDisposable
{
    private static readonly (string Column, GtfsWeekdays Day)[] _weekdays =
    [
        ("monday", GtfsWeekdays.Monday), ("tuesday", GtfsWeekdays.Tuesday), ("wednesday", GtfsWeekdays.Wednesday),
        ("thursday", GtfsWeekdays.Thursday), ("friday", GtfsWeekdays.Friday), ("saturday", GtfsWeekdays.Saturday),
        ("sunday", GtfsWeekdays.Sunday),
    ];

    /// <summary>Opens a snapshot zip file for reading.</summary>
    /// <param name="path">Path to the zip.</param>
    public static GtfsFeed Open(string path) => new(ZipFile.OpenRead(path));

    /// <summary>Whether the snapshot has a file; <c>calendar.txt</c> and <c>calendar_dates.txt</c> are each optional.</summary>
    /// <param name="fileName">File name inside the zip, e.g. <c>calendar.txt</c>.</param>
    public bool Contains(string fileName) => zip.GetEntry(fileName) is not null;

    /// <summary>Rows of <c>routes.txt</c>.</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsRoute> Routes()
    {
        using var table = OpenTable("routes.txt", "route_id", "route_type");
        int id = table.Column("route_id"), type = table.Column("route_type");
        int shortName = table.OptionalColumn("route_short_name"), longName = table.OptionalColumn("route_long_name");
        while (table.Read())
        {
            yield return new(table.Text(id), table.OptionalText(shortName), table.OptionalText(longName), table.WholeNumber(type));
        }
    }

    /// <summary>Rows of <c>stops.txt</c>.</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsStop> Stops()
    {
        using var table = OpenTable("stops.txt", "stop_id");
        int id = table.Column("stop_id"), name = table.OptionalColumn("stop_name");
        int lat = table.OptionalColumn("stop_lat"), lon = table.OptionalColumn("stop_lon");
        int locationType = table.OptionalColumn("location_type"), parent = table.OptionalColumn("parent_station");
        while (table.Read())
        {
            yield return new(
                table.Text(id), table.OptionalText(name), table.OptionalNumber(lat), table.OptionalNumber(lon),
                table.OptionalWholeNumber(locationType) ?? 0, table.OptionalText(parent));
        }
    }

    /// <summary>Rows of <c>trips.txt</c>.</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsTrip> Trips()
    {
        using var table = OpenTable("trips.txt", "trip_id", "route_id", "service_id");
        int id = table.Column("trip_id"), route = table.Column("route_id"), service = table.Column("service_id");
        int direction = table.OptionalColumn("direction_id"), shape = table.OptionalColumn("shape_id");
        int headsign = table.OptionalColumn("trip_headsign");
        while (table.Read())
        {
            yield return new(
                table.Text(id), table.Text(route), table.Text(service), table.OptionalWholeNumber(direction),
                table.OptionalText(shape), table.OptionalText(headsign));
        }
    }

    /// <summary>Rows of <c>stop_times.txt</c>, in file order (usually grouped by trip).</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsStopTime> StopTimes()
    {
        using var table = OpenTable("stop_times.txt", "trip_id", "stop_sequence", "stop_id", "arrival_time", "departure_time");
        int trip = table.Column("trip_id"), sequence = table.Column("stop_sequence"), stop = table.Column("stop_id");
        int arrival = table.Column("arrival_time"), departure = table.Column("departure_time");
        int distance = table.OptionalColumn("shape_dist_traveled");
        while (table.Read())
        {
            yield return new(
                table.Text(trip), table.WholeNumber(sequence), table.Text(stop), table.OptionalTime(arrival),
                table.OptionalTime(departure), table.OptionalNumber(distance));
        }
    }

    /// <summary>Rows of <c>shapes.txt</c>, in file order.</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsShapePoint> ShapePoints()
    {
        using var table = OpenTable("shapes.txt", "shape_id", "shape_pt_sequence", "shape_pt_lat", "shape_pt_lon");
        int id = table.Column("shape_id"), sequence = table.Column("shape_pt_sequence");
        int lat = table.Column("shape_pt_lat"), lon = table.Column("shape_pt_lon");
        int distance = table.OptionalColumn("shape_dist_traveled");
        while (table.Read())
        {
            yield return new(table.Text(id), table.WholeNumber(sequence), table.Number(lat), table.Number(lon), table.OptionalNumber(distance));
        }
    }

    /// <summary>Rows of <c>calendar.txt</c>.</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsCalendar> Calendars()
    {
        using var table = OpenTable(
            "calendar.txt", "service_id", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "start_date", "end_date");
        int id = table.Column("service_id"), start = table.Column("start_date"), end = table.Column("end_date");
        while (table.Read())
        {
            var days = GtfsWeekdays.None;
            foreach (var (column, day) in _weekdays)
            {
                var index = table.Column(column);
                days |= table.WholeNumber(index) switch
                {
                    0 => GtfsWeekdays.None,
                    1 => day,
                    _ => throw table.Error(index, "must be 0 or 1"),
                };
            }

            yield return new(table.Text(id), days, table.Date(start), table.Date(end));
        }
    }

    /// <summary>Rows of <c>calendar_dates.txt</c>.</summary>
    /// <exception cref="GtfsFormatException">The file is missing or breaks the format.</exception>
    public IEnumerable<GtfsCalendarDate> CalendarDates()
    {
        using var table = OpenTable("calendar_dates.txt", "service_id", "date", "exception_type");
        int id = table.Column("service_id"), date = table.Column("date"), type = table.Column("exception_type");
        while (table.Read())
        {
            var added = table.WholeNumber(type) switch
            {
                1 => true,
                2 => false,
                _ => throw table.Error(type, "must be 1 (added) or 2 (removed)"),
            };
            yield return new(table.Text(id), table.Date(date), added);
        }
    }

    /// <inheritdoc />
    public void Dispose() => zip.Dispose();

    private GtfsTable OpenTable(string fileName, params ReadOnlySpan<string> requiredColumns)
    {
        var entry = zip.GetEntry(fileName) ?? throw new GtfsFormatException(fileName, "the file is missing from the snapshot");
        return GtfsTable.Open(fileName, entry.Open(), requiredColumns);
    }
}

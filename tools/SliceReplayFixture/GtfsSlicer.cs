using System.Globalization;
using System.IO.Compression;
using System.Text;
using Sylvan.Data.Csv;

namespace SliceReplayFixture;

/// <summary>Keeps only the GTFS rows needed for the chosen route ids on the given service days.</summary>
internal static class GtfsSlicer
{
    public static void Slice(string zipPath, string outputDirectory, HashSet<string> routeIds, IReadOnlyList<DateOnly> serviceDays)
    {
        if (serviceDays.Count == 0)
        {
            throw new ArgumentException("At least one service day is required.", nameof(serviceDays));
        }

        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);

        using var zip = ZipFile.OpenRead(zipPath);
        var routes = Keep(zip, "routes.txt", row => routeIds.Contains(row["route_id"]));
        Write(outputDirectory, "routes.txt", routes);

        var tripsAll = Keep(zip, "trips.txt", row => routeIds.Contains(row["route_id"]));
        var candidateServices = ColumnSet(tripsAll, "service_id");

        var calendarAll = Keep(zip, "calendar.txt", row => candidateServices.Contains(row["service_id"]));
        var datesAll = Keep(zip, "calendar_dates.txt", row => candidateServices.Contains(row["service_id"]));
        var activeServices = ActiveServices(calendarAll, datesAll, serviceDays);

        var trips = FilterRows(tripsAll, row => activeServices.Contains(row["service_id"]));
        Write(outputDirectory, "trips.txt", trips);
        var tripIds = ColumnSet(trips, "trip_id");
        var shapeIds = ColumnSet(trips, "shape_id");

        var stopTimes = Keep(zip, "stop_times.txt", row => tripIds.Contains(row["trip_id"]));
        Write(outputDirectory, "stop_times.txt", stopTimes);
        var stopIds = ColumnSet(stopTimes, "stop_id");

        // Parent stations referenced by kept stops must stay, or snapshot checks reject the feed.
        var stopsAll = Keep(zip, "stops.txt", _ => true);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in stopsAll.Rows)
        {
            if (stopIds.Contains(row["stop_id"]) && row.TryGetValue("parent_station", out var parent) && parent.Trim().Length > 0)
            {
                parents.Add(parent.Trim());
            }
        }

        var stops = FilterRows(stopsAll, row => stopIds.Contains(row["stop_id"]) || parents.Contains(row["stop_id"]));
        Write(outputDirectory, "stops.txt", stops);

        var shapes = Keep(zip, "shapes.txt", row => shapeIds.Contains(row["shape_id"]));
        Write(outputDirectory, "shapes.txt", shapes);

        var calendar = FilterRows(calendarAll, row => activeServices.Contains(row["service_id"]));
        Write(outputDirectory, "calendar.txt", calendar);

        var calendarDates = FilterRows(datesAll, row => activeServices.Contains(row["service_id"]));
        Write(outputDirectory, "calendar_dates.txt", calendarDates);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  routes={routes.Rows.Count} trips={tripIds.Count} stop_times={stopTimes.Rows.Count} " +
            $"stops={stops.Rows.Count} shapes_pts={shapes.Rows.Count} services={activeServices.Count} " +
            $"(of {candidateServices.Count} on route)"));
    }

    /// <summary>
    /// Same rules as <c>TripScheduleReader</c>: calendar weekdays in range minus removals, plus added exceptions.
    /// </summary>
    private static HashSet<string> ActiveServices(CsvTable calendar, CsvTable dates, IReadOnlyList<DateOnly> days)
    {
        var removed = new HashSet<(string Service, DateOnly Date)>();
        var added = new HashSet<(string Service, DateOnly Date)>();
        foreach (var row in dates.Rows)
        {
            var service = row["service_id"];
            var date = ParseGtfsDate(row["date"]);
            // GTFS exception_type: 1 = added, 2 = removed.
            if (row["exception_type"] == "1")
            {
                added.Add((service, date));
            }
            else if (row["exception_type"] == "2")
            {
                removed.Add((service, date));
            }
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var day in days)
        {
            // Match TripScheduleReader: ISO Monday=bit0 … Sunday=bit6.
            var bit = day.DayOfWeek == DayOfWeek.Sunday ? 1 << 6 : 1 << ((int)day.DayOfWeek - 1);
            foreach (var row in calendar.Rows)
            {
                var service = row["service_id"];
                var start = ParseGtfsDate(row["start_date"]);
                var end = ParseGtfsDate(row["end_date"]);
                if (day < start || day > end || removed.Contains((service, day)))
                {
                    continue;
                }

                var mask = WeekdayMask(row);
                if ((mask & bit) != 0)
                {
                    active.Add(service);
                }
            }

            foreach (var (service, date) in added)
            {
                if (date == day)
                {
                    active.Add(service);
                }
            }
        }

        return active;
    }

    private static int WeekdayMask(Dictionary<string, string> row)
    {
        // Matches Postgres storage: Monday = bit 0 … Sunday = bit 6.
        var mask = 0;
        if (row["monday"] == "1")
        {
            mask |= 1 << 0;
        }

        if (row["tuesday"] == "1")
        {
            mask |= 1 << 1;
        }

        if (row["wednesday"] == "1")
        {
            mask |= 1 << 2;
        }

        if (row["thursday"] == "1")
        {
            mask |= 1 << 3;
        }

        if (row["friday"] == "1")
        {
            mask |= 1 << 4;
        }

        if (row["saturday"] == "1")
        {
            mask |= 1 << 5;
        }

        if (row["sunday"] == "1")
        {
            mask |= 1 << 6;
        }

        return mask;
    }

    private static DateOnly ParseGtfsDate(string value) =>
        DateOnly.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);

    private static CsvTable Keep(ZipArchive zip, string entryName, Func<Dictionary<string, string>, bool> keep)
    {
        var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException($"GTFS zip has no {entryName}", entryName);
        using var stream = entry.Open();
        using var reader = CsvDataReader.Create(new StreamReader(stream, Encoding.UTF8));
        var header = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            header[i] = reader.GetName(i);
        }

        var rows = new List<Dictionary<string, string>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++)
            {
                row[header[i]] = reader.GetString(i);
            }

            if (keep(row))
            {
                rows.Add(row);
            }
        }

        return new CsvTable(header, rows);
    }

    private static CsvTable FilterRows(CsvTable table, Func<Dictionary<string, string>, bool> keep) =>
        new(table.Header, table.Rows.Where(keep).ToList());

    private static HashSet<string> ColumnSet(CsvTable table, string column)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            if (row.TryGetValue(column, out var value) && value.Length > 0)
            {
                set.Add(value);
            }
        }

        return set;
    }

    private static void Write(string directory, string fileName, CsvTable table)
    {
        var path = Path.Combine(directory, fileName);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(string.Join(',', table.Header));
        foreach (var row in table.Rows)
        {
            writer.WriteLine(string.Join(',', table.Header.Select(name => Escape(row[name]))));
        }
    }

    private static string Escape(string value) =>
        value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private sealed record CsvTable(string[] Header, List<Dictionary<string, string>> Rows);
}

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Sylvan.Data.Csv;

namespace SliceReplayFixture;

/// <summary>Keeps only the GTFS rows needed for the chosen route ids.</summary>
internal static class GtfsSlicer
{
    public static void Slice(string zipPath, string outputDirectory, HashSet<string> routeIds)
    {
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);

        using var zip = ZipFile.OpenRead(zipPath);
        var routes = Keep(zip, "routes.txt", row => routeIds.Contains(row["route_id"]));
        Write(outputDirectory, "routes.txt", routes);

        var trips = Keep(zip, "trips.txt", row => routeIds.Contains(row["route_id"]));
        Write(outputDirectory, "trips.txt", trips);
        var tripIds = ColumnSet(trips, "trip_id");
        var serviceIds = ColumnSet(trips, "service_id");
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

        var calendar = Keep(zip, "calendar.txt", row => serviceIds.Contains(row["service_id"]));
        Write(outputDirectory, "calendar.txt", calendar);

        var calendarDates = Keep(zip, "calendar_dates.txt", row => serviceIds.Contains(row["service_id"]));
        Write(outputDirectory, "calendar_dates.txt", calendarDates);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  routes={routes.Rows.Count} trips={tripIds.Count} stop_times={stopTimes.Rows.Count} " +
            $"stops={stops.Rows.Count} shapes_pts={shapes.Rows.Count} services={serviceIds.Count}"));
    }

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

using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Infrastructure.Tests.Gtfs;

public sealed class GtfsSnapshotCheckTests
{
    [Fact]
    public void Real_snapshot_slice_is_valid()
    {
        var report = Check(GtfsFixture.Tram9Files());

        Assert.True(report.IsValid, string.Join("; ", report.Problems));
        Assert.Equal(81, report.Rows["stop_times.txt"]);
        Assert.Equal(678, report.Rows["shapes.txt"]);
    }

    [Fact]
    public void Export_with_empty_core_files_is_rejected_for_the_first_one_alone()
    {
        // The shape of HSL's broken export of 2026-10-27: zero-byte routes, stops, trips and shapes, intact stop times.
        var files = GtfsFixture.Tram9Files();
        files["routes.txt"] = files["stops.txt"] = files["trips.txt"] = files["shapes.txt"] = "";

        var report = Check(files);

        Assert.False(report.IsValid);
        Assert.Equal(["routes.txt: the file is empty, without a header row"], report.Problems);
    }

    [Fact]
    public void Snapshot_with_only_calendar_dates_is_valid()
    {
        // Entur ships no calendar.txt: every service day is listed in calendar_dates.txt.
        var files = GtfsFixture.Tram9Files();
        files.Remove("calendar.txt");
        files["calendar_dates.txt"] = "service_id,date,exception_type\n1009_20261030_20261101_Ke,20261030,1\n";

        Assert.True(Check(files).IsValid);
    }

    [Fact]
    public void Snapshot_without_any_calendar_is_rejected()
    {
        var files = GtfsFixture.Tram9Files();
        files.Remove("calendar.txt");
        files.Remove("calendar_dates.txt");

        var report = Check(files);

        Assert.Contains("neither calendar.txt nor calendar_dates.txt is present", report.Problems);
        Assert.Contains(report.Problems, problem => problem.StartsWith("trips.txt: service_id in neither calendar: 3 ", StringComparison.Ordinal));
    }

    [Fact]
    public void Dangling_stop_is_counted_once_per_rule_with_its_first_case()
    {
        var files = GtfsFixture.Tram9Files();
        var firstCall = files["stop_times.txt"].Split('\n')[1].Split(',');
        var (trip, stop) = (firstCall[0], firstCall[3]);
        var calls = files["stop_times.txt"].Split('\n').Count(line => line.Split(',') is [_, _, _, var id, ..] && id == stop);
        files["stops.txt"] = string.Join('\n', files["stops.txt"].Split('\n').Where(line => !line.StartsWith(stop + ",", StringComparison.Ordinal)));

        var report = Check(files);

        Assert.Equal([$"stop_times.txt: stop_id not in stops.txt: {calls} (first: {trip} #1 -> {stop})"], report.Problems);
    }

    [Fact]
    public void Trip_with_a_single_stop_time_is_rejected()
    {
        var files = GtfsFixture.Tram9Files();
        var lines = files["stop_times.txt"].Split('\n');
        var trip = lines[1].Split(',')[0];
        files["stop_times.txt"] = string.Join('\n', lines.Where((line, i) => i <= 1 || !line.StartsWith(trip + ",", StringComparison.Ordinal)));

        Assert.Equal([$"trips.txt: trip with fewer than 2 stop times: 1 (first: {trip} (1))"], Check(files).Problems);
    }

    [Fact]
    public void Duplicate_trip_route_and_parent_references_are_reported()
    {
        var files = GtfsFixture.Tram9Files();
        var tripLines = files["trips.txt"].TrimEnd('\n').Split('\n');
        files["trips.txt"] = string.Join('\n', [.. tripLines, tripLines[1].Replace("1009,", "9999,", StringComparison.Ordinal)]) + "\n";
        var stopLines = files["stops.txt"].TrimEnd('\n').Split('\n');
        stopLines[1] = stopLines[1].Replace(",0, ,", ",0,NOPE,", StringComparison.Ordinal);
        files["stops.txt"] = string.Join('\n', stopLines) + "\n";

        var report = Check(files);

        Assert.Equal(3, report.Problems.Count);
        Assert.Contains(report.Problems, problem => problem.StartsWith("trips.txt: duplicate trip_id: 1 ", StringComparison.Ordinal));
        Assert.Contains(report.Problems, problem => problem.StartsWith("trips.txt: route_id not in routes.txt: 1 ", StringComparison.Ordinal));
        Assert.Contains(report.Problems, problem => problem.StartsWith("stops.txt: parent_station not in stops.txt: 1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_times_file_with_a_header_only_leaves_every_trip_without_calls()
    {
        var files = GtfsFixture.Tram9Files();
        files["stop_times.txt"] = files["stop_times.txt"].Split('\n')[0] + "\n";

        var report = Check(files);

        Assert.Contains("stop_times.txt has no rows", report.Problems);
        Assert.Contains(report.Problems, problem => problem.StartsWith("trips.txt: trip with fewer than 2 stop times: 3 ", StringComparison.Ordinal));
    }

    private static GtfsSnapshotReport Check(Dictionary<string, string> files)
    {
        using var feed = GtfsFixture.Feed(files);
        return GtfsSnapshotCheck.Run(feed);
    }
}

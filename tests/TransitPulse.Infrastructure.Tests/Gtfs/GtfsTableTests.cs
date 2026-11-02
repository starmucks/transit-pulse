using System.Text;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Infrastructure.Tests.Gtfs;

public sealed class GtfsTableTests
{
    [Fact]
    public void Finds_columns_by_header_name_in_any_order()
    {
        using var table = Open("stop_id,trip_id,arrival_time\nS1,T1,25:10:00\n", "trip_id", "stop_id");

        Assert.True(table.Read());
        Assert.Equal("T1", table.Text(table.Column("trip_id")));
        Assert.Equal("S1", table.Text(table.Column("stop_id")));
        Assert.Equal(new TimeSpan(1, 1, 10, 0), table.OptionalTime(table.OptionalColumn("arrival_time")));
        Assert.False(table.Read());
    }

    [Fact]
    public void Reads_quoted_fields_with_commas_quotes_and_non_ascii_text_after_a_byte_order_mark()
    {
        var csv = "trip_id,trip_headsign\nT1,\"Käpylä via Lasipalatsi, \"\"express\"\"\"\n";
        using var table = GtfsTable.Open("trips.txt", new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)]), "trip_id");

        Assert.True(table.Read());
        Assert.Equal("T1", table.Text(table.Column("trip_id")));
        Assert.Equal("Käpylä via Lasipalatsi, \"express\"", table.OptionalText(table.OptionalColumn("trip_headsign")));
    }

    [Fact]
    public void Absent_or_empty_optional_fields_read_as_null()
    {
        using var table = Open("shape_id,shape_dist_traveled\nA,\n", "shape_id");

        Assert.True(table.Read());
        Assert.Null(table.OptionalNumber(table.Column("shape_dist_traveled")));
        Assert.Null(table.OptionalWholeNumber(table.OptionalColumn("direction_id")));
        Assert.Null(table.OptionalText(table.OptionalColumn("trip_headsign")));
        Assert.Equal(-1, table.OptionalColumn("direction_id"));
    }

    [Fact]
    public void Numbers_tolerate_surrounding_spaces()
    {
        using var table = Open("stop_lat,stop_sequence\n 60.188027 , 7\n");

        Assert.True(table.Read());
        Assert.Equal(60.188027, table.Number(table.Column("stop_lat")));
        Assert.Equal(7, table.WholeNumber(table.Column("stop_sequence")));
    }

    [Fact]
    public void File_without_a_header_row_is_rejected()
    {
        // The broken HSL export of 2026-10-27 had zero-byte routes, stops, trips and shapes files.
        var error = Assert.Throws<GtfsFormatException>(() => Open("", "route_id"));

        Assert.Equal("routes.txt: the file is empty, without a header row", error.Message);
    }

    [Fact]
    public void Missing_required_column_is_rejected()
    {
        var error = Assert.Throws<GtfsFormatException>(() => Open("route_id,route_type\nR1,3\n", "route_id", "route_short_name"));

        Assert.Equal("routes.txt: required column route_short_name is missing", error.Message);
    }

    [Theory]
    [InlineData("route_id,route_type\nR1,3\n,3\n", "data row 2, route_id must not be empty: ''")]
    [InlineData("route_id,route_type\nR1,3\nR2,bus\n", "data row 2, route_type is not an integer: 'bus'")]
    public void Bad_value_names_its_row_column_and_text(string csv, string expected)
    {
        using var table = Open(csv, "route_id", "route_type");
        Assert.True(table.Read());
        table.Text(table.Column("route_id"));
        table.WholeNumber(table.Column("route_type"));
        Assert.True(table.Read());

        var error = Assert.Throws<GtfsFormatException>(() =>
        {
            table.Text(table.Column("route_id"));
            table.WholeNumber(table.Column("route_type"));
        });

        Assert.Equal($"routes.txt: {expected}", error.Message);
    }

    [Theory]
    [InlineData("stop_lat\nNaN\n")]
    [InlineData("stop_lat\n60.1.8\n")]
    public void Coordinates_that_are_not_finite_numbers_are_rejected(string csv)
    {
        using var table = Open(csv);
        Assert.True(table.Read());

        Assert.Throws<GtfsFormatException>(() => table.Number(table.Column("stop_lat")));
    }

    private static GtfsTable Open(string csv, params string[] requiredColumns) =>
        GtfsTable.Open("routes.txt", new MemoryStream(Encoding.UTF8.GetBytes(csv)), requiredColumns);
}

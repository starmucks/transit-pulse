using Npgsql;
using TransitPulse.Infrastructure.Database;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Integration.Tests.Postgres;

/// <summary>Stage 3.3: catalogue reads for the API against an imported GTFS snapshot.</summary>
public sealed class GtfsCatalogueReaderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gtfs-catalogue-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Routes_match_the_imported_tram_slice()
    {
        var reader = new GtfsCatalogueReader(await ImportTram9Async());

        var routes = await reader.ListRoutesAsync("hsl", Ct);

        var route = Assert.Single(routes!);
        Assert.Equal("1009", route.RouteId);
        Assert.Equal("9", route.ShortName);
        Assert.Contains("Länsisatama", route.LongName, StringComparison.Ordinal);
        Assert.Equal(0, route.RouteType);
    }

    [Fact]
    public async Task Stops_include_coordinates_from_the_slice()
    {
        var reader = new GtfsCatalogueReader(await ImportTram9Async());

        var stops = await reader.ListStopsAsync("hsl", Ct);

        Assert.NotNull(stops);
        Assert.NotEmpty(stops);
        Assert.Contains(stops, s => s.StopId == "1020453" && s.Name == "Päärautatieasema");
        Assert.All(stops, s =>
        {
            Assert.NotNull(s.Lat);
            Assert.NotNull(s.Lon);
            Assert.InRange(s.Lat.Value, 60.0, 61.0);
            Assert.InRange(s.Lon.Value, 24.0, 26.0);
        });
    }

    [Fact]
    public async Task Route_shapes_return_ordered_points_for_tram_9()
    {
        var reader = new GtfsCatalogueReader(await ImportTram9Async());

        var result = await reader.ListRouteShapesAsync("hsl", "1009", Ct);

        Assert.True(result!.RouteFound);
        Assert.NotEmpty(result.Shapes);
        Assert.All(result.Shapes, shape =>
        {
            Assert.False(string.IsNullOrWhiteSpace(shape.ShapeId));
            Assert.True(shape.Points.Count >= 2, shape.ShapeId);
        });
    }

    [Fact]
    public async Task Unknown_route_is_reported_without_shapes()
    {
        var reader = new GtfsCatalogueReader(await ImportTram9Async());

        var result = await reader.ListRouteShapesAsync("hsl", "no-such-route", Ct);

        Assert.False(result!.RouteFound);
        Assert.Empty(result.Shapes);
    }

    [Fact]
    public async Task Feed_without_a_snapshot_has_no_catalogue()
    {
        var reader = new GtfsCatalogueReader(await ImportTram9Async(feedId: "mbta"));

        Assert.Null(await reader.ActiveSnapshotAsync("hsl", Ct));
        Assert.Null(await reader.ListRoutesAsync("hsl", Ct));
        Assert.Null(await reader.ListStopsAsync("hsl", Ct));
        Assert.Null(await reader.ListRouteShapesAsync("hsl", "1009", Ct));
    }

    [Fact]
    public async Task Fixed_snapshot_id_reads_match_the_active_catalogue()
    {
        var database = await ImportTram9Async();
        var reader = new GtfsCatalogueReader(database);
        var snapshotId = await reader.ActiveSnapshotAsync("hsl", Ct);

        var byId = await reader.ListRoutesAsync(snapshotId!.Value, Ct);
        var byFeed = await reader.ListRoutesAsync("hsl", Ct);

        Assert.Equal(byFeed!.Select(r => r.RouteId), byId!.Select(r => r.RouteId));
        Assert.Null(await reader.ListRoutesAsync(snapshotId: 1_000_000, Ct));
        Assert.Null(await reader.ListStopsAsync(snapshotId: 1_000_000, Ct));
        Assert.Null(await reader.ListRouteShapesAsync(snapshotId: 1_000_000, "1009", Ct));
    }

    [Fact]
    public async Task Fixed_snapshot_shapes_survive_after_a_newer_import()
    {
        var database = await ImportTram9Async();
        var reader = new GtfsCatalogueReader(database);
        var firstId = (await reader.ActiveSnapshotAsync("hsl", Ct))!.Value;

        var second = await new GtfsImporter(database).ImportAsync(
            "hsl",
            GtfsZips.Tram9(Path.Combine(_directory, "newer.zip"), files =>
            {
                files["routes.txt"] =
                    "route_id,agency_id,route_short_name,route_long_name,route_desc,route_type,route_url\n" +
                    "1009,HSL,9,Renamed for catalogue test,,0,http://example.test\n";
            }),
            Ct);
        Assert.True(second.Report!.IsValid, string.Join("; ", second.Report.Problems));
        Assert.NotEqual(firstId, second.SnapshotId);

        // Old partitions are dropped with the snapshot row; fixed-id read must not invent an empty catalogue.
        Assert.Null(await reader.ListRoutesAsync(firstId, Ct));

        var active = Assert.Single((await reader.ListRoutesAsync(second.SnapshotId!.Value, Ct))!);
        Assert.Equal("Renamed for catalogue test", active.LongName);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<NpgsqlDataSource> ImportTram9Async(string feedId = "hsl")
    {
        var database = await postgres.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(database, Ct);
        var zip = GtfsZips.Tram9(Path.Combine(_directory, "snapshot.zip"));
        var result = await new GtfsImporter(database).ImportAsync(feedId, zip, Ct);
        Assert.True(result.Report!.IsValid, string.Join("; ", result.Report.Problems));
        return database;
    }
}

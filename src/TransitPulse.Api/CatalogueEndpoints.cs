namespace TransitPulse.Api;

/// <summary>Versioned REST endpoints for the active GTFS catalogue (routes, stops, shapes).</summary>
public static class CatalogueEndpoints
{
    /// <summary>Maps <c>/api/v1/...</c> catalogue routes onto <paramref name="app"/>.</summary>
    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").WithTags("Catalogue");

        v1.MapGet("/feeds/{feedId}/routes", ListRoutesAsync)
            .WithName("ListRoutes")
            .WithSummary("Lists routes in the feed's active GTFS snapshot.");

        v1.MapGet("/feeds/{feedId}/stops", ListStopsAsync)
            .WithName("ListStops")
            .WithSummary("Lists stops with coordinates in the feed's active GTFS snapshot.");

        v1.MapGet("/feeds/{feedId}/routes/{routeId}/shapes", ListRouteShapesAsync)
            .WithName("ListRouteShapes")
            .WithSummary("Lists distinct shapes of a route in the feed's active GTFS snapshot.");

        return app;
    }

    private static async Task<IResult> ListRoutesAsync(
        string feedId, GtfsCatalogueCache catalogue, CancellationToken cancellationToken)
    {
        var routes = await catalogue.GetRoutesAsync(feedId, cancellationToken);
        return routes is null ? TypedResults.NotFound() : TypedResults.Ok(routes);
    }

    private static async Task<IResult> ListStopsAsync(
        string feedId, GtfsCatalogueCache catalogue, CancellationToken cancellationToken)
    {
        var stops = await catalogue.GetStopsAsync(feedId, cancellationToken);
        return stops is null ? TypedResults.NotFound() : TypedResults.Ok(stops);
    }

    private static async Task<IResult> ListRouteShapesAsync(
        string feedId, string routeId, GtfsCatalogueCache catalogue, CancellationToken cancellationToken)
    {
        var result = await catalogue.GetRouteShapesAsync(feedId, routeId, cancellationToken);
        if (result is null || !result.RouteFound)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(result.Shapes);
    }
}

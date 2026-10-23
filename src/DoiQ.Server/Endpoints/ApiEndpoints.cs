namespace DoiQ.Server.Endpoints;

/// <summary>Composes the endpoint groups. Each group lives in its own file.</summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapDoiQApi(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints()
           .MapAuthEndpoints()
           .MapManagementEndpoints()
           .MapActorsEndpoints()
           .MapBoardEndpoints()
           .MapTaskEndpoints()
           .MapTaskDetailEndpoints()
           .MapFeedEndpoints();

        app.MapFallback("/api/{**rest}", () => Results.NotFound());   // unknown /api routes must not fall through to the SPA index.html
        return app;
    }
}

using DoiQ.Core.Services;

namespace DoiQ.Server.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        // MCP itself is POST-only (stateless streamable HTTP). A GET with a valid agent key is a health check for the
        // agent's MCP settings: 200 names the agent the key belongs to; a missing or wrong key gets 401.
        app.MapGet("/mcp", (ICurrentActor me) => me.Current is { } a
            ? Results.Ok(new { status = "ok", actor = a.Name, kind = a.Kind.ToString(), mcp = "POST JSON-RPC 2.0 requests to this URL" })
            : Results.Unauthorized());
        return app;
    }
}

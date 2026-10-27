using DoiQ.Core.Services;
using DoiQ.Server.Auth;
using DoiQ.Shared.Dtos;

namespace DoiQ.Server.Endpoints;

/// <summary>
/// Boards. Listing is open to every signed-in caller (agents need the ids); add, rename and delete are the user only.
/// Moving a task to another board is a task route (POST /api/tasks/{id}/move-to-board).
/// </summary>
public static class BoardEndpoints
{
    public static IEndpointRouteBuilder MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/boards", async (BoardService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct)));

        // What the board view reads: its active tasks in one call, the archive in pages, and the search over both.
        app.MapGet("/api/boards/{id:int}/tasks", async (int id, TaskService s, CancellationToken ct) => Results.Ok(await s.LoadBoard(id, ct)));
        app.MapGet("/api/boards/{id:int}/archive", async (int id, string? q, string? cursor, int? limit, TaskService s, CancellationToken ct) =>
            Results.Ok(await s.GetArchive(id, q, cursor, limit ?? TaskService.DefaultPageSize, ct)));
        app.MapGet("/api/boards/{id:int}/search", async (int id, string? q, int? archivedLimit, TaskService s, CancellationToken ct) =>
            Results.Ok(await s.SearchBoard(id, q, archivedLimit ?? 10, ct)));

        var admin = app.MapGroup("/api/boards").RequireAuthorization(Policies.UserOnly);
        admin.MapPost("", async (CreateBoardRequest req, BoardService s, CancellationToken ct) =>
        {
            var b = await s.CreateAsync(req, ct);
            return Results.Created($"/api/boards/{b.Id}", b);
        });
        admin.MapPut("/{id:int}", async (int id, RenameBoardRequest req, BoardService s, CancellationToken ct) => Results.Ok(await s.RenameAsync(id, req, ct)));
        // confirm=true is required: a bare DELETE changes nothing.
        admin.MapDelete("/{id:int}", async (int id, bool? confirm, BoardService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, confirm ?? false, ct);
            return Results.NoContent();
        });
        return app;
    }
}

using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;

namespace DoiQ.Server.Endpoints;

/// <summary>Tasks themselves: query, create, edit, move, assign, pool claims and the seen/handled markers.</summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/tasks", async (TaskService s, CancellationToken ct, string? status, string? assignee, string? q, string? sort, string? dir, bool? includeArchived, bool? needsMe, int? boardId, string? boardName) =>
        {
            BoardStatus? st = string.IsNullOrWhiteSpace(status) ? null : EnumInput.Parse<BoardStatus>(status, "status");
            return Results.Ok(await s.List(new TaskQuery
            {
                Status = st, Assignee = assignee, Q = q, Sort = sort, Dir = dir,
                IncludeArchived = includeArchived ?? false, NeedsMe = needsMe ?? false, BoardId = boardId, BoardName = boardName,
            }, ct));
        });
        api.MapGet("/tasks/{id:int}", async (int id, TaskService s, CancellationToken ct) => Results.Ok(await s.GetAsync(id, ct)));
        api.MapPost("/tasks", async (CreateTaskRequest req, TaskService s, CancellationToken ct) =>
        {
            var r = await s.Create(req, ct);
            return r.Created ? Results.Created($"/api/tasks/{r.Task.Task.Id}", r.Task) : Results.Ok(r.Task);
        });
        api.MapPatch("/tasks/{id:int}", async (int id, UpdateTaskRequest req, TaskService s, CancellationToken ct) => Results.Ok(await s.UpdateAsync(id, req, ct)));
        api.MapDelete("/tasks/{id:int}", async (int id, TaskService s, CancellationToken ct) => { await s.DeleteAsync(id, ct); return Results.NoContent(); });
        api.MapPost("/tasks/{id:int}/move", async (int id, MoveTaskRequest req, TaskService s, CancellationToken ct) => Results.Ok(await s.Move(id, req, ct)));
        api.MapPost("/tasks/{id:int}/assign", async (int id, AssignRequest req, TaskService s, CancellationToken ct) => Results.Ok(await s.AssignAsync(id, req, ct)));

        api.MapPost("/tasks/{id:int}/claim", async (int id, TaskService s, CancellationToken ct) => Results.Ok(await s.ClaimAsync(id, ct)));
        api.MapPost("/tasks/{id:int}/renew", async (int id, TaskService s, CancellationToken ct) => Results.Ok(await s.RenewClaimAsync(id, ct)));
        api.MapPost("/tasks/{id:int}/release", async (int id, TaskService s, CancellationToken ct) => Results.Ok(await s.ReleaseAsync(id, ct)));

        api.MapPost("/tasks/{id:int}/seen", async (int id, HttpRequest http, TaskService s, CancellationToken ct) =>
        {
            await s.MarkSeen(id, (await ReadOptionalAsync<SeenRequest>(http, ct))?.ThroughEventId, ct);
            return Results.NoContent();
        });
        api.MapPost("/tasks/{id:int}/handled", async (int id, HttpRequest http, TaskService s, CancellationToken ct) =>
        {
            await s.MarkHandled(id, (await ReadOptionalAsync<SeenRequest>(http, ct))?.ThroughEventId, ct);
            return Results.NoContent();
        });
        return app;
    }

    /// <summary>The body if there is one. Not JSON (or not sent as JSON) is the caller's mistake: 400, not 500.</summary>
    private static async Task<T?> ReadOptionalAsync<T>(HttpRequest http, CancellationToken ct) where T : class
    {
        if (http.ContentLength is not > 0 && http.Headers.TransferEncoding.Count == 0) return null;
        try { return await http.ReadFromJsonAsync<T>(ct); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new ValidationException("The body must be JSON (Content-Type: application/json), for example {\"throughEventId\": 42}, or empty.");
        }
    }
}

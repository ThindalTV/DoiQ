using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;

namespace DoiQ.Server.Endpoints;

/// <summary>What hangs off a task: info fields, the comment thread and subtasks.</summary>
public static class TaskDetailEndpoints
{
    public static IEndpointRouteBuilder MapTaskDetailEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/tasks/{id:int}");

        api.MapPost("/fields", async (int id, FieldRequest req, TaskService s, CancellationToken ct) =>
            Results.Created($"/api/tasks/{id}", await s.AddFieldAsync(id, req, ct)));
        // Upsert by label (agents re-running a step must not pile up duplicates).
        api.MapPut("/fields", async (int id, FieldRequest req, TaskService s, CancellationToken ct) => Results.Ok(await s.UpsertFieldAsync(id, req, ct)));
        api.MapPut("/fields/{fieldId:int}", async (int id, int fieldId, FieldRequest req, TaskService s, CancellationToken ct) => Results.Ok(await s.UpdateFieldAsync(id, fieldId, req, ct)));
        api.MapDelete("/fields/{fieldId:int}", async (int id, int fieldId, TaskService s, CancellationToken ct) => { await s.DeleteFieldAsync(id, fieldId, ct); return Results.NoContent(); });

        api.MapGet("/comments", async (int id, TaskService s, CancellationToken ct) => Results.Ok(await s.GetCommentsAsync(id, ct)));
        api.MapPost("/comments", async (int id, AddCommentRequest req, TaskService s, CancellationToken ct) =>
            Results.Created($"/api/tasks/{id}", await s.AddComment(id, req, ct)));

        api.MapPost("/subtasks", async (int id, AddSubtaskRequest req, TaskService s, CancellationToken ct) =>
            Results.Created($"/api/tasks/{id}", await s.AddSubtask(id, req, ct)));
        api.MapPatch("/subtasks/{subId:int}", async (int id, int subId, UpdateSubtaskRequest req, TaskService s, CancellationToken ct) => Results.Ok(await s.UpdateSubtaskAsync(id, subId, req, ct)));
        api.MapDelete("/subtasks/{subId:int}", async (int id, int subId, TaskService s, CancellationToken ct) => { await s.DeleteSubtaskAsync(id, subId, ct); return Results.NoContent(); });
        return app;
    }
}

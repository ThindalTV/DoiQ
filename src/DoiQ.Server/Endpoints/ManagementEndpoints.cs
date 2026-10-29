using DoiQ.Core.Services;
using DoiQ.Server.Auth;
using DoiQ.Shared.Dtos;

namespace DoiQ.Server.Endpoints;

/// <summary>Account and agent administration. The user (cookie) only: API-key callers are authenticated but get 403.</summary>
public static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api").RequireAuthorization(Policies.UserOnly);

        // Every other session is signed out; this one gets a cookie with the new stamp so it stays signed in.
        admin.MapPost("/account/password", async (ChangePasswordRequest req, HttpContext ctx, AccountService s, ActorService actors, CancellationToken ct) =>
        {
            await s.Change(req.Current, req.New, ct);
            await AuthEndpoints.SignInUser(ctx, actors, s, ct);
            return Results.NoContent();
        });

        admin.MapGet("/agents", async (AgentAdminService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct)));
        admin.MapPost("/agents", async (CreateAgentRequest req, AgentAdminService s, CancellationToken ct) =>
        {
            var r = await s.CreateAsync(req, ct);
            return Results.Created($"/api/agents/{r.Agent.Id}", r);
        });
        admin.MapPut("/agents/{id:int}", async (int id, UpdateAgentRequest req, AgentAdminService s, CancellationToken ct) => Results.Ok(await s.UpdateAsync(id, req, ct)));
        admin.MapPost("/agents/{id:int}/rotate-key", async (int id, AgentAdminService s, CancellationToken ct) => Results.Ok(await s.RotateKeyAsync(id, ct)));
        admin.MapPost("/agents/{id:int}/deactivate", async (int id, AgentAdminService s, CancellationToken ct) => Results.Ok(await s.SetActiveAsync(id, false, ct)));
        admin.MapPost("/agents/{id:int}/activate", async (int id, AgentAdminService s, CancellationToken ct) => Results.Ok(await s.SetActiveAsync(id, true, ct)));
        admin.MapDelete("/agents/{id:int}", async (int id, AgentAdminService s, CancellationToken ct) => Results.Ok(await s.Delete(id, ct)));
        // Gives the agent a task asking it to describe its responsibilities (it answers with PUT /api/me/responsibilities).
        admin.MapPost("/agents/{id:int}/ask-responsibilities", async (int id, TaskService s, CancellationToken ct) =>
        {
            var r = await s.AskForResponsibilities(id, ct);
            return Results.Created($"/api/tasks/{r.Task.Task.Id}", r);
        });

        // Orchestrator agents managing other agents by name (the service checks the flag; everyone else gets 403).
        var orchestrator = app.MapGroup("/api/orchestrator/agents");
        orchestrator.MapPost("", async (RegisterAgentRequest req, ICurrentActor me, AgentAdminService s, CancellationToken ct) =>
        {
            var r = await s.Register(me.Require().Id, req, ct);
            return Results.Created($"/api/orchestrator/agents/{Uri.EscapeDataString(r.Agent.Name)}", r);
        });
        orchestrator.MapPatch("/{name}", async (string name, ConfigureAgentRequest req, ICurrentActor me, AgentAdminService s, CancellationToken ct) =>
            Results.Ok(await s.Configure(me.Require().Id, name, req, ct)));
        orchestrator.MapDelete("/{name}", async (string name, ICurrentActor me, AgentAdminService s, CancellationToken ct) =>
            Results.Ok(await s.Delete(me.Require().Id, name, ct)));
        return app;
    }
}

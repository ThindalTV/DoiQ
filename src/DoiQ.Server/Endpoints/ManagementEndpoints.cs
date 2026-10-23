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

        admin.MapPost("/account/password", async (ChangePasswordRequest req, AccountService s, CancellationToken ct) =>
        {
            await s.Change(req.Current, req.New, ct);
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
        return app;
    }
}

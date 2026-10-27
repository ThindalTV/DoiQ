using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;

namespace DoiQ.Server.Endpoints;

/// <summary>Who is calling, and who exists.</summary>
public static class ActorsEndpoints
{
    public static IEndpointRouteBuilder MapActorsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/me", (ICurrentActor me) => me.Current is { } a ? Results.Ok(new ActorDto(a.Id, a.Kind, a.Name, a.Initials, a.IsTriage, a.IsDispatcher)) : Results.Unauthorized());
        api.MapGet("/actors", (TaskService s, CancellationToken ct) => s.GetActorsAsync(ct));
        // An agent describing what it does, for the dispatcher (the user edits it in Settings instead).
        api.MapPut("/me/responsibilities", async (SetResponsibilitiesRequest req, ICurrentActor me, AgentAdminService s, CancellationToken ct) =>
        {
            var a = me.Require();
            if (!a.IsAgent) throw new ForbiddenException("Only agents set their own responsibilities; edit an agent's in Settings.");
            return Results.Ok(await s.SetResponsibilities(a.Id, req.Responsibilities, ct));
        });
        return app;
    }
}

using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;

namespace DoiQ.Server.Endpoints;

/// <summary>Who is calling, and who exists.</summary>
public static class ActorsEndpoints
{
    public static IEndpointRouteBuilder MapActorsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/me", (ICurrentActor me) => me.Current is { } a ? Results.Ok(new ActorDto(a.Id, a.Kind, a.Name, a.Initials, a.IsTriage)) : Results.Unauthorized());
        api.MapGet("/actors", (TaskService s, CancellationToken ct) => s.GetActorsAsync(ct));
        return app;
    }
}

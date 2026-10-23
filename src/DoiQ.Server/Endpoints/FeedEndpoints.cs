using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;

namespace DoiQ.Server.Endpoints;

/// <summary>What changed and what needs attention: the caller's inbox and the activity-event feed used for hub catch-up.</summary>
public static class FeedEndpoints
{
    public static IEndpointRouteBuilder MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/inbox", async (TaskService s, CancellationToken ct) => Results.Ok(await s.GetInboxAsync(ct)));
        // boardId scopes the feed to one board; omitted = every board (each event carries its boardId and boardName).
        api.MapGet("/events/latest", async (TaskService s, CancellationToken ct, int? boardId) => Results.Ok(new LatestEventDto(await s.GetLatestEventIdAsync(boardId, ct))));
        api.MapGet("/events", async (TaskService s, CancellationToken ct, long? after, int? limit, int? boardId) => Results.Ok(await s.GetEventsAsync(after ?? 0, limit ?? 100, boardId, ct)));
        return app;
    }
}

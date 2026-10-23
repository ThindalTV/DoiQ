using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DoiQ.Server.Hubs;

/// <summary>Push-only: clients listen, all writes go through REST or MCP. Browser (cookie) only; no API key.</summary>
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
public sealed class BoardHub : Hub;

public sealed class SignalRBroadcaster(IHubContext<BoardHub> hub) : IBoardBroadcaster
{
    public Task TaskChangedAsync(TaskChangedMessage m) => hub.Clients.All.SendAsync(BoardHubEvents.TaskChanged, m);
    public Task TaskDeletedAsync(TaskDeletedMessage m) => hub.Clients.All.SendAsync(BoardHubEvents.TaskDeleted, m);
    public Task CommentAddedAsync(CommentAddedMessage m) => hub.Clients.All.SendAsync(BoardHubEvents.CommentAdded, m);
    public Task FieldChangedAsync(FieldChangedMessage m) => hub.Clients.All.SendAsync(BoardHubEvents.FieldChanged, m);
    public Task SeenChangedAsync(SeenChangedMessage m) => hub.Clients.All.SendAsync(BoardHubEvents.SeenChanged, m);
    public Task BoardChangedAsync(BoardChangedMessage m) => hub.Clients.All.SendAsync(BoardHubEvents.BoardChanged, m);
}

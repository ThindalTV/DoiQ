global using DoiQ.Shared.Dtos;

namespace DoiQ.Client.Models;

/// <summary>A failed API call with a message fit to show the user. Status is null when the server could not be reached.</summary>
public class ApiException(string message, System.Net.HttpStatusCode? status = null) : Exception(message)
{
    public System.Net.HttpStatusCode? Status { get; } = status;

    /// <summary>The call never got an answer (network down, server restarting).</summary>
    public bool IsOffline => Status is null;
}

/// <summary>
/// A task write carried an out-of-date Version (HTTP 409). By the time this is thrown the client has already reloaded
/// the task from the server, so the board shows the winner's version; ChangedBy is who made it.
/// </summary>
public sealed class TaskConflictException(int taskId, string changedBy, string message)
    : ApiException(message, System.Net.HttpStatusCode.Conflict)
{
    public int TaskId { get; } = taskId;
    /// <summary>Server actor name of whoever changed the task last ("User" is the signed-in human).</summary>
    public string ChangedBy { get; } = changedBy;
}

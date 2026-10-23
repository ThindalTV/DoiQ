using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;

namespace DoiQ.Core.Services;

public sealed record ActorInfo(int Id, ActorKind Kind, string Name, string Initials, bool IsTriage)
{
    public bool IsHuman => Kind == ActorKind.Human;
    public bool IsAgent => Kind == ActorKind.Agent;
}

/// <summary>Who is making the current call. Hidden behind an interface so real auth can replace it later.</summary>
public interface ICurrentActor
{
    ActorInfo? Current { get; }
}

public static class CurrentActorExtensions
{
    public static ActorInfo Require(this ICurrentActor a) => a.Current ?? throw new UnauthorizedException();
}

/// <summary>Pushes committed changes to browsers. Called by TaskService only after SaveChanges succeeded.</summary>
public interface IBoardBroadcaster
{
    Task TaskChangedAsync(TaskChangedMessage m);
    Task TaskDeletedAsync(TaskDeletedMessage m);
    Task CommentAddedAsync(CommentAddedMessage m);
    Task FieldChangedAsync(FieldChangedMessage m);
    /// <summary>A seen marker moved (read receipts). Optional for implementers.</summary>
    Task SeenChangedAsync(SeenChangedMessage m) => Task.CompletedTask;
    /// <summary>A board was added, renamed or deleted. Optional for implementers.</summary>
    Task BoardChangedAsync(BoardChangedMessage m) => Task.CompletedTask;
}

public sealed class NoopBroadcaster : IBoardBroadcaster
{
    public Task TaskChangedAsync(TaskChangedMessage m) => Task.CompletedTask;
    public Task TaskDeletedAsync(TaskDeletedMessage m) => Task.CompletedTask;
    public Task CommentAddedAsync(CommentAddedMessage m) => Task.CompletedTask;
    public Task FieldChangedAsync(FieldChangedMessage m) => Task.CompletedTask;
}

/// <summary>Wakes the outbox worker after rows were written.</summary>
public interface IOutboxSignal
{
    void Signal();
}

public sealed class NoopOutboxSignal : IOutboxSignal
{
    public void Signal() { }
}

public class UnauthorizedException() : Exception("Not authenticated.");
public class ForbiddenException(string message) : Exception(message);
public class NotFoundException(string message) : Exception(message);
public class ConflictException(string message) : Exception(message);

/// <summary>A board cannot be deleted while it holds tasks that are not Done. OpenTasks is the count that blocks it.</summary>
public sealed class BoardNotEmptyException(string boardName, int openTasks, int totalTasks)
    : ConflictException($"Board '{boardName}' still has {openTasks} task{(openTasks == 1 ? "" : "s")} that {(openTasks == 1 ? "is" : "are")} not done (it holds {totalTasks} in all). Finish or delete {(openTasks == 1 ? "it" : "them")} first.")
{
    public int OpenTasks { get; } = openTasks;
    public int TotalTasks { get; } = totalTasks;
}

/// <summary>A write carried an ExpectedVersion that is no longer current. ChangedBy = whoever touched the task last.</summary>
public sealed class StaleVersionException(int taskId, long currentVersion, long expectedVersion, string changedBy)
    : ConflictException($"Task {taskId} changed (version {currentVersion}, you had {expectedVersion}) by {changedBy}. Reload and retry.")
{
    public int TaskId { get; } = taskId;
    public long CurrentVersion { get; } = currentVersion;
    public long ExpectedVersion { get; } = expectedVersion;
    public string ChangedBy { get; } = changedBy;
}
public class ValidationException(string message) : Exception(message);

public sealed class TaskQuery
{
    public BoardStatus? Status { get; set; }
    /// <summary>"me", "any" (pool), "none"/"unassigned", "agents" (any named agent) or an actor name.</summary>
    public string? Assignee { get; set; }
    public string? Q { get; set; }
    /// <summary>updated (default), created, lane, due, title.</summary>
    public string? Sort { get; set; }
    /// <summary>asc or desc; sensible default per sort.</summary>
    public string? Dir { get; set; }
    public bool IncludeArchived { get; set; }
    public bool NeedsMe { get; set; }
    /// <summary>The board to list (id wins over name). Neither = the only board; with several boards that is an error listing them.</summary>
    public int? BoardId { get; set; }
    public string? BoardName { get; set; }
}

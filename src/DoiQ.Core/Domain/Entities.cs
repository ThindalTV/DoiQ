using DoiQ.Shared.Enums;

namespace DoiQ.Core.Domain;

public class Actor
{
    public int Id { get; set; }
    public ActorKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Initials { get; set; } = "";
    /// <summary>SHA-256 hex of the agent's API key.</summary>
    public string? ApiKeyHash { get; set; }
    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }
    public string? NotifyUrl { get; set; }
    public bool IsTriage { get; set; }
    /// <summary>Gets the "any agent" tasks and passes each to the agent best placed to do it (itself included). At most one.</summary>
    public bool IsDispatcher { get; set; }
    /// <summary>What the agent does and does not do, in its own words or the user's; the dispatcher reads it.</summary>
    public string? Responsibilities { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    /// <summary>Last successful API-key authentication (written at most once a minute).</summary>
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>Small key/value store for settings managed from the UI (e.g. "password-hash").</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A kanban board. Boards are fully separate: a task belongs to exactly one and never moves.</summary>
public class Board
{
    public int Id { get; set; }
    /// <summary>1-60 characters, trimmed, unique case-insensitively.</summary>
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>Optimistic concurrency token for renames, incremented by BoardService.</summary>
    public long Version { get; set; } = 1;
}

public class TaskItem
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public BoardStatus Status { get; set; }
    public int? AssigneeId { get; set; }
    /// <summary>"Any agent" pool.</summary>
    public bool AssignedToPool { get; set; }
    public DateOnly? DueDate { get; set; }
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DoneAt { get; set; }
    /// <summary>Higher = more important = higher in the lane.</summary>
    public double SortOrder { get; set; }
    public TaskSource Source { get; set; }
    public string? SourceRef { get; set; }
    public DateTime LastActivityAt { get; set; }
    public int LastActivityById { get; set; }
    /// <summary>Optimistic concurrency token, incremented by TaskService (SQLite has no rowversion).</summary>
    public long Version { get; set; }
    public int? ClaimedById { get; set; }
    public DateTime? ClaimExpiresAt { get; set; }
    /// <summary>Agent comments that @mentioned another agent since the user last commented (the loop guard).</summary>
    public int AgentMentionHops { get; set; }

    public List<InfoField> Fields { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<Subtask> Subtasks { get; set; } = [];
}

public class Subtask
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public int? AssigneeId { get; set; }
    public double SortOrder { get; set; }
    public int CreatedById { get; set; }
    public DateTime? DoneAt { get; set; }
}

public class InfoField
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public int AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Comment
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int AuthorId { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
}

/// <summary>An @mention in a comment: the mentioned actor is asked for input. Open until it answers (comment, field or mark_handled).</summary>
public class Mention
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int CommentId { get; set; }
    public Comment? Comment { get; set; }
    public int MentionedId { get; set; }
    public int ById { get; set; }
    /// <summary>The CommentAdded event that made it (not a FK, like ActivityEvent.TaskId).</summary>
    public long EventId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
}

/// <summary>The change log; its Id is the polling/catch-up cursor. TaskId is deliberately not a FK so Deleted events survive.</summary>
public class ActivityEvent
{
    public long Id { get; set; }
    public int BoardId { get; set; }
    public int TaskId { get; set; }
    public int ActorId { get; set; }
    public EventType Type { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime At { get; set; }
}

public class ReadMarker
{
    public int TaskId { get; set; }
    public int ActorId { get; set; }
    public long SeenThroughEventId { get; set; }
    public long HandledThroughEventId { get; set; }
}

public enum OutboxKind
{
    Todo = 0,
    Notify = 1,
}

public class OutboxMessage
{
    public long Id { get; set; }
    public OutboxKind Kind { get; set; }
    public int BoardId { get; set; }
    public int TaskId { get; set; }
    /// <summary>Todo: the agent to wake. Notify: the agent whose NotifyUrl delivers to the user.</summary>
    public int TargetActorId { get; set; }
    /// <summary>Latest event this wake-up covers (rows are coalesced per task+target while unsent).</summary>
    public long EventId { get; set; }
    public ActivityEvent? Event { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NotBefore { get; set; }
    public int Attempts { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Idempotency record: a retried write with the same (Actor, Key) returns the first result.</summary>
public class RequestKey
{
    public int Id { get; set; }
    public int ActorId { get; set; }
    public string Key { get; set; } = "";
    /// <summary>Id of the entity the first call created (task, comment or subtask depending on the key scope).</summary>
    public int Result { get; set; }
    public DateTime CreatedAt { get; set; }
}

using DoiQ.Shared.Enums;

namespace DoiQ.Shared.Dtos;

// Conventions
//  * All timestamps are UTC.
//  * "Assignee" strings: null = unassigned, "any" = the agent pool, otherwise an actor name (e.g. "User", "Hermes").
//  * SortOrder: higher = more important = displayed HIGHER in the lane (render lanes by SortOrder descending).
//  * ExpectedVersion (optional): the Version the caller last saw; a mismatch returns HTTP 409.

/// <summary>IsDispatcher: this agent gets the "any agent" tasks and passes each one on. Responsibilities: what an agent does (null for the user).</summary>
public sealed record ActorDto(int Id, ActorKind Kind, string Name, string Initials, bool IsTriage, bool IsDispatcher = false, string? Responsibilities = null);

public sealed record TaskSummaryDto(
    int Id,
    string Title,
    BoardStatus Status,
    string? Assignee,
    int? AssigneeId,
    bool AssignedToPool,
    DateOnly? DueDate,
    double SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DoneAt,
    DateTime LastActivityAt,
    string LastActivityBy,
    long Version,
    TaskSource Source,
    string? SourceRef,
    string? ClaimedBy,
    DateTime? ClaimExpiresAt,
    int CommentCount,
    int FieldCount,
    int SubtaskTotal,
    int SubtaskDone,
    /// <summary>Someone else touched the task after the caller's SeenThroughEventId (per caller; false in hub broadcasts).</summary>
    bool Unread,
    /// <summary>Caller is human and (task is assigned to them in Waiting, or Unread).</summary>
    bool NeedsMe,
    /// <summary>The board the task lives on. A task never changes board.</summary>
    int BoardId = 0,
    string BoardName = "",
    /// <summary>In Done for longer than the archive window: off the board, listed in the board's archive view. Moving it out of Done brings it back.</summary>
    bool Archived = false);

/// <summary>
/// GET /api/boards/{id}/tasks: everything the board view needs in one call. Tasks = the board's active (not archived)
/// tasks, at most Board:MaxBoardTasks of them (Truncated says some were left out). LatestEventId is read before the
/// tasks, so a client that catches up from it misses nothing.
/// </summary>
public sealed record BoardLoadDto(int BoardId, long LatestEventId, IReadOnlyList<TaskSummaryDto> Tasks, int ArchivedCount, int ArchiveAfterDays, bool Truncated = false);

/// <summary>One page of tasks. NextCursor (opaque) fetches the next page; null when there is none.</summary>
public sealed record TaskPageDto(IReadOnlyList<TaskSummaryDto> Tasks, string? NextCursor);

/// <summary>GET /api/boards/{id}/search: the matching active tasks (ids, they are on the board already) and the first archived matches.</summary>
public sealed record BoardSearchDto(IReadOnlyList<int> ActiveTaskIds, TaskPageDto Archived);

/// <summary>A board with its task counts. OpenTasks = not in Done, DoneTasks = in Done (archived ones included). Version changes on rename.</summary>
public sealed record BoardDto(int Id, string Name, int OpenTasks, int DoneTasks, DateTime CreatedAt, long Version = 1);

public sealed record CreateBoardRequest(string Name);

/// <summary>ExpectedVersion (optional): the board Version the caller last saw; a mismatch returns HTTP 409.</summary>
public sealed record RenameBoardRequest(string Name, long? ExpectedVersion = null);

public sealed record InfoFieldDto(int Id, int TaskId, string Label, string Value, int AuthorId, string Author, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// EventId = the CommentAdded event that created it (0 when unknown); compare with a ReadMarker's SeenThroughEventId.
/// Unread = someone else wrote it after the caller's SeenThroughEventId (per caller; false in hub broadcasts and in write answers).
/// Mentions = the actors it @mentioned (asked for input), with whether each has answered yet.
/// </summary>
public sealed record CommentDto(int Id, int TaskId, int AuthorId, string Author, string Body, DateTime CreatedAt, DateTime? EditedAt, long EventId = 0, bool Unread = false,
    IReadOnlyList<MentionDto>? Mentions = null);

/// <summary>Answered: the mentioned actor has since commented, written a field or marked the task handled.</summary>
public sealed record MentionDto(int ActorId, string Name, bool Answered);

/// <summary>How far another actor has seen a task: everything through SeenThroughEventId (comments with an EventId at or below it).</summary>
public sealed record SeenByDto(int ActorId, string Actor, ActorKind Kind, long SeenThroughEventId);

public sealed record SubtaskDto(int Id, int TaskId, string Title, bool Done, string? Assignee, int? AssigneeId, double SortOrder, string CreatedBy, DateTime? DoneAt);

public sealed record TaskDetailDto(
    TaskSummaryDto Task,
    string Description,
    string CreatedBy,
    IReadOnlyList<InfoFieldDto> Fields,
    IReadOnlyList<CommentDto> Comments,
    IReadOnlyList<SubtaskDto> Subtasks,
    long LatestEventId,
    /// <summary>The caller's own seen position on this task.</summary>
    long SeenThroughEventId = 0,
    /// <summary>The other actors that have seen part of this task (read receipts).</summary>
    IReadOnlyList<SeenByDto>? SeenBy = null);

// ---- Requests ----

public sealed record NewFieldDto(string Label, string Value);

public sealed record CreateTaskRequest(
    string Title,
    string? Description = null,
    BoardStatus? Status = null,
    string? Assignee = null,
    DateOnly? DueDate = null,
    IReadOnlyList<NewFieldDto>? Fields = null,
    TaskSource? Source = null,
    string? SourceRef = null,
    string? RequestKey = null,
    /// <summary>The board to create on (id wins over name). Omitted = the only board when there is exactly one, otherwise an error listing the boards.</summary>
    int? BoardId = null,
    string? BoardName = null);

public sealed record CreateTaskResult(TaskDetailDto Task, bool Created);

public sealed record UpdateTaskRequest(string? Title = null, string? Description = null, DateOnly? DueDate = null, bool ClearDueDate = false, long? ExpectedVersion = null);

/// <summary>BeforeTaskId = place directly above that task, AfterTaskId = place directly below it. Neither = bottom of the target lane.</summary>
public sealed record MoveTaskRequest(BoardStatus Status, int? BeforeTaskId = null, int? AfterTaskId = null, long? ExpectedVersion = null);

/// <summary>The board to move a task to, by id or name (id wins). The task keeps its lane and goes to the bottom of it.</summary>
public sealed record MoveToBoardRequest(int? BoardId = null, string? BoardName = null, long? ExpectedVersion = null);

public sealed record AssignRequest(string? Assignee, long? ExpectedVersion = null);

public sealed record FieldRequest(string Label, string Value);

public sealed record AddCommentRequest(string Body, string? RequestKey = null);

/// <summary>An agent hands a task back to the user: Question says what it needs from them.</summary>
public sealed record AskUserRequest(string? Question, string? RequestKey = null);

public sealed record SeenRequest(long? ThroughEventId = null);

public sealed record AddSubtaskRequest(string Title, string? Assignee = null, int? BeforeId = null, int? AfterId = null, string? RequestKey = null);

/// <summary>Assignee: null = unchanged, "" or "none" = unassign, otherwise an actor name.</summary>
public sealed record UpdateSubtaskRequest(string? Title = null, bool? Done = null, string? Assignee = null, int? BeforeId = null, int? AfterId = null);

public sealed record LoginRequest(string Password);

// ---- First-run setup and agent management (the user only, except setup) ----

public sealed record SetupStatusDto(bool NeedsSetup);

/// <summary>POST /api/setup (anonymous, only while NeedsSetup; signs the user in). 409 afterwards.</summary>
public sealed record SetupRequest(string Password);

/// <summary>POST /api/account/password. 400 on a wrong Current or an empty/short New.</summary>
public sealed record ChangePasswordRequest(string Current, string New);

/// <summary>Never contains the API key or the webhook secret.</summary>
public sealed record AgentAdminDto(
    int Id,
    string Name,
    string Initials,
    bool IsTriage,
    bool IsActive,
    string? WebhookUrl,
    string? NotifyUrl,
    bool HasWebhookSecret,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    bool IsDispatcher = false,
    string? Responsibilities = null,
    bool IsOrchestrator = false);

/// <summary>
/// Blank WebhookSecret = the server generates one and returns it once. At most one agent is triage and one is the
/// dispatcher (setting the flag takes it from whoever had it); one agent may be both. Any number may be orchestrators.
/// </summary>
public sealed record CreateAgentRequest(string Name, bool IsTriage = false, string? Initials = null, string? WebhookUrl = null, string? WebhookSecret = null, string? NotifyUrl = null,
    bool IsDispatcher = false, string? Responsibilities = null, bool IsOrchestrator = false);

/// <summary>Full replace of name, initials, flags, responsibilities, webhookUrl and notifyUrl (null/blank clears it). WebhookSecret only changes when non-blank.</summary>
public sealed record UpdateAgentRequest(string Name, bool IsTriage = false, string? Initials = null, string? WebhookUrl = null, string? WebhookSecret = null, string? NotifyUrl = null,
    bool IsDispatcher = false, string? Responsibilities = null, bool IsOrchestrator = false);

/// <summary>
/// POST /api/orchestrator/agents (orchestrator agents only): register an agent. It gets no triage, dispatcher or
/// orchestrator role; only the user hands those out. Blank WebhookSecret = the server generates one and returns it once.
/// </summary>
public sealed record RegisterAgentRequest(string Name, string? Initials = null, string? WebhookUrl = null, string? WebhookSecret = null, string? NotifyUrl = null,
    string? Responsibilities = null);

/// <summary>
/// PATCH /api/orchestrator/agents/{name} (orchestrator agents only). Null leaves a value as it is and "" clears it;
/// WebhookSecret can only be replaced, not cleared. RotateKey issues a new API key (returned once; the old one stops working).
/// </summary>
public sealed record ConfigureAgentRequest(string? Initials = null, string? WebhookUrl = null, string? WebhookSecret = null, string? NotifyUrl = null,
    string? Responsibilities = null, bool RotateKey = false);

/// <summary>ApiKey is set only when the key was rotated, and is shown exactly once.</summary>
public sealed record ConfiguredAgentResult(AgentAdminDto Agent, string? ApiKey);

/// <summary>
/// KeptForHistory: the agent had taken part in tasks, so it stays in their history under Name (now "Old name (deleted)")
/// with no key, hooks or roles; otherwise it is gone entirely. Either way the old name is free again.
/// </summary>
public sealed record DeletedAgentResult(int Id, string Name, bool KeptForHistory);

/// <summary>PUT /api/me/responsibilities (agents): the agent describes what it does, for the dispatcher. Blank clears it.</summary>
public sealed record SetResponsibilitiesRequest(string? Responsibilities);

/// <summary>ApiKey (and a generated WebhookSecret) are shown exactly once.</summary>
public sealed record CreatedAgentResult(AgentAdminDto Agent, string ApiKey, string? WebhookSecret);

public sealed record RotatedKeyResult(string ApiKey);

// ---- Inbox / events ----

public sealed record InboxItemDto(TaskSummaryDto Task, string Reason, long LatestEventId);

public sealed record EventDto(long Id, int TaskId, EventType Type, int ActorId, string Actor, ActorKind ActorKind, DateTime At, string? Payload, int BoardId = 0, string BoardName = "");

public sealed record EventFeedDto(IReadOnlyList<EventDto> Events, long NextCursor, bool HasMore);

/// <summary>GET /api/events/latest: where a client that is about to load the board starts its catch-up from.</summary>
public sealed record LatestEventDto(long LatestEventId);

// ---- SignalR (push-only) ----

public static class BoardHubEvents
{
    public const string HubPath = "/hubs/board";
    public const string TaskChanged = nameof(TaskChanged);
    public const string TaskDeleted = nameof(TaskDeleted);
    public const string CommentAdded = nameof(CommentAdded);
    public const string FieldChanged = nameof(FieldChanged);
    /// <summary>An actor moved its seen marker on a task. Not an activity event: it has no event id and is not in /api/events.</summary>
    public const string SeenChanged = nameof(SeenChanged);
    /// <summary>A board was added, renamed or deleted. Not an activity event: no event id, not in /api/events.</summary>
    public const string BoardChanged = nameof(BoardChanged);
}

// Every hub message is sent to all browsers; BoardId says which board it belongs to, so a client applies only the board it shows.

/// <summary>Task.Unread/NeedsMe are not caller-specific here; use ActorId to decide whether it is your own change. Task.BoardId is the board.</summary>
public sealed record TaskChangedMessage(long EventId, EventType Type, int ActorId, ActorKind ActorKind, TaskSummaryDto Task);

public sealed record TaskDeletedMessage(long EventId, int ActorId, int TaskId, int BoardId = 0);

public sealed record CommentAddedMessage(long EventId, int ActorId, ActorKind ActorKind, int TaskId, CommentDto Comment, int BoardId = 0);

public sealed record SeenChangedMessage(int TaskId, int ActorId, ActorKind ActorKind, string Actor, long SeenThroughEventId, int BoardId = 0);

/// <summary>Field is null when the field was deleted.</summary>
public sealed record FieldChangedMessage(long EventId, int ActorId, ActorKind ActorKind, int TaskId, int FieldId, InfoFieldDto? Field, int BoardId = 0);

public sealed record BoardChangedMessage(BoardChangeKind Kind, int BoardId, string Name);

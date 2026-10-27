using System.ComponentModel;
using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DoiQ.Server.Mcp;

/// <summary>
/// MCP adapter over TaskService. Every tool acts as the agent whose API key made the call (the actor comes from the
/// request principal, never from a tool argument). The descriptions are the agent's instructions; the rules themselves
/// are enforced in TaskService and surface as tool errors whose text says what to do instead.
/// </summary>
[McpServerToolType]
public sealed class DoiQTools(TaskService tasks, BoardService boards)
{
    private static BoardStatus ParseStatus(string s) => EnumInput.Parse<BoardStatus>(s, "status");

    private static DateOnly? ParseDue(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : DateOnly.TryParse(s, out var d) ? d : throw new ValidationException("dueDate must be yyyy-MM-dd.");

    private static TaskSource? ParseSource(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : EnumInput.Parse<TaskSource>(s, "source");

    /// <summary>
    /// The MCP SDK only passes the message of an McpException on to the model; any other exception becomes a generic
    /// "an error occurred". Board rule violations carry advice the agent can act on, so translate them.
    /// </summary>
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or NotFoundException or ConflictException or UnauthorizedException)
        {
            var kind = ex switch
            {
                ValidationException => "Invalid request",
                ForbiddenException => "Not allowed",
                NotFoundException => "Not found",
                ConflictException => "Conflict (reload with get_task and retry)",
                _ => "Not authenticated",
            };
            throw new McpException($"{kind}: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "list_boards", ReadOnly = true), Description("The boards, each with id, name and how many tasks are open (not Done) and done. Boards are fully separate and a task never moves between them. Your inbox spans every board; every task you get back says which board it is on (boardId, boardName). You cannot add, rename or delete boards: only the user can.")]
    public Task<IReadOnlyList<BoardDto>> ListBoards(CancellationToken ct) => Run(() => boards.ListAsync(ct));

    [McpServerTool(Name = "get_inbox", ReadOnly = true), Description("Your queue across all boards (each item's task has boardId and boardName), oldest first: New tasks you triage (triage agent only), tasks or subtasks assigned to you that you have not handled or that the user touched since, and unclaimed pool ('any') tasks. Work through it, then call mark_handled for each task you acted on or parked so it leaves the queue until the user acts again. Run this whenever you are woken and as a periodic sweep.")]
    public Task<IReadOnlyList<InboxItemDto>> GetInbox(CancellationToken ct) => Run(() => tasks.GetInboxAsync(ct));

    [McpServerTool(Name = "get_changes", ReadOnly = true), Description("Events since a cursor (event id), across all boards unless you pass boardId or boardName; each event says which board it is on. Store the returned nextCursor and pass it next time. Your own changes are left out. If hasMore is true, call again with nextCursor.")]
    public Task<EventFeedDto> GetChanges([Description("Last event id you processed; 0 for the beginning")] long after = 0, int limit = 100,
        [Description("Only this board (id)")] int? boardId = null, [Description("Only this board (name); boardId wins")] string? boardName = null, CancellationToken ct = default) =>
        Run(async () =>
        {
            int? scope = boardId is null && string.IsNullOrWhiteSpace(boardName) ? null : await tasks.FindBoardIdAsync(boardId, boardName, ct);
            return await tasks.GetEventsAsync(after, limit, scope, ct);
        });

    [McpServerTool(Name = "search_tasks", ReadOnly = true), Description("Search/list tasks on one board, a page at a time. Pass boardId or boardName; with only one board that is the default, with several boards and none given you get an error listing them (see list_boards). q matches title, description, field labels and values, comments and checklist lines. assignee: 'me', 'any' (pool), 'none', 'agents', or a name. sort: updated (default), created, lane, due, title. Archived tasks (Done for more than 14 days) are left out unless includeArchived; each task says archived=true/false. Returns {tasks, nextCursor}: when nextCursor is not null, call again with the same arguments and cursor=nextCursor for the next page.")]
    public Task<TaskPageDto> SearchTasks(string? q = null, string? status = null, string? assignee = null, bool includeArchived = false, string? sort = null,
        [Description("Board id")] int? boardId = null, [Description("Board name (case-insensitive); boardId wins")] string? boardName = null,
        [Description("nextCursor from the previous page; omit for the first page")] string? cursor = null,
        [Description("Tasks per page, 1-200 (default 50)")] int limit = TaskService.DefaultPageSize, CancellationToken ct = default) =>
        Run(() => tasks.Search(new TaskQuery { Q = q, Status = string.IsNullOrWhiteSpace(status) ? null : ParseStatus(status), Assignee = assignee, IncludeArchived = includeArchived, Sort = sort, BoardId = boardId, BoardName = boardName }, cursor, limit, ct));

    [McpServerTool(Name = "get_task", ReadOnly = true), Description("A full task with info fields, subtasks and comments, plus latestEventId (pass it to mark_seen / mark_handled). Read this before acting on a task you were woken for.")]
    public Task<TaskDetailDto> GetTask(int taskId, CancellationToken ct) => Run(() => tasks.GetAsync(taskId, ct));

    [McpServerTool(Name = "create_task"), Description("Create a task on a board: pass boardId or boardName (with only one board that is the default; with several and none given you get an error listing them, see list_boards). A task stays on its board for good. For tasks derived from an email set source='Email' and sourceRef=<message id>: creating the same sourceRef twice returns the existing task (created=false). Pass requestKey to make a retried call safe. Status defaults to New; a task you create goes to the top of its lane as top priority; assignee is a name, 'any' (pool) or omitted. You may only create a task in Doing or Done if you assign it to yourself or to the user.")]
    public Task<CreateTaskResult> CreateTask(string title, string? description = null, string? status = null, string? assignee = null, string? dueDate = null,
        [Description("Initial info fields")] NewFieldDto[]? fields = null, string? source = null, string? sourceRef = null, string? requestKey = null,
        [Description("Board id")] int? boardId = null, [Description("Board name (case-insensitive); boardId wins")] string? boardName = null, CancellationToken ct = default) =>
        Run(() => tasks.Create(new CreateTaskRequest(title, description, string.IsNullOrWhiteSpace(status) ? null : ParseStatus(status), assignee, ParseDue(dueDate), fields,
            ParseSource(source), sourceRef, requestKey, boardId, boardName), ct));

    [McpServerTool(Name = "update_task"), Description("Edit title, description and/or due date (yyyy-MM-dd). Pass expectedVersion (from get_task) to avoid overwriting a concurrent change; on a conflict reload with get_task and retry.")]
    public Task<TaskSummaryDto> UpdateTask(int taskId, string? title = null, string? description = null, string? dueDate = null, bool clearDueDate = false, long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.UpdateAsync(taskId, new UpdateTaskRequest(title, description, ParseDue(dueDate), clearDueDate, expectedVersion), ct));

    [McpServerTool(Name = "move_task"), Description("Change column (New, Waiting, Doing, Done) and/or position: beforeTaskId places it directly above that task, afterTaskId directly below. You may move tasks assigned to you and tasks assigned to the user (the user often works a task through you: keep its status current for them; it stays assigned to the user). A pool or unassigned task goes to Doing only while you hold its claim, and to Done only once it is assigned to you. Tasks another agent holds you cannot move: add_comment instead.")]
    public Task<TaskSummaryDto> MoveTask(int taskId, string status, int? beforeTaskId = null, int? afterTaskId = null, long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.Move(taskId, new MoveTaskRequest(ParseStatus(status), beforeTaskId, afterTaskId, expectedVersion), ct));

    [McpServerTool(Name = "assign_task"), Description("Assign to a person or agent by name (e.g. 'User', 'Hermes'), to 'any' (the agent pool), or 'none' to unassign. Assigning a task to the user notifies them, so do it when you need their decision or have finished work they should review.")]
    public Task<TaskSummaryDto> AssignTask(int taskId, string assignee, long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.AssignAsync(taskId, new AssignRequest(assignee, expectedVersion), ct));

    [McpServerTool(Name = "upsert_field"), Description("Add or update an info field by label (e.g. 'Email summary', 'Research', 'Result'). Re-running a step updates the same field instead of adding duplicates. Value is markdown.")]
    public Task<InfoFieldDto> UpsertField(int taskId, string label, string value, CancellationToken ct) => Run(() => tasks.UpsertFieldAsync(taskId, new FieldRequest(label, value), ct));

    [McpServerTool(Name = "add_comment"), Description("Post to the task's discussion thread (markdown). Allowed on every task, including tasks another agent holds: there it is the only thing you can do, so use it to ask questions or report. Pass requestKey to make a retry safe.")]
    public Task<CommentDto> AddComment(int taskId, string body, string? requestKey = null, CancellationToken ct = default) => Run(() => tasks.AddComment(taskId, new AddCommentRequest(body, requestKey), ct));

    [McpServerTool(Name = "mark_seen"), Description("Move your SeenThroughEventId on a task (throughEventId defaults to the latest event). Writes never imply seen.")]
    public Task<string> MarkSeen(int taskId, long? throughEventId = null, CancellationToken ct = default) => Run(async () => { await tasks.MarkSeen(taskId, throughEventId, ct); return "ok"; });

    [McpServerTool(Name = "mark_handled"), Description("Record that you acted on or parked this task up to throughEventId (defaults to the latest event). It leaves your inbox until the user acts on it again. Call it after you finish a task from get_inbox, also when you decided to do nothing.")]
    public Task<string> MarkHandled(int taskId, long? throughEventId = null, CancellationToken ct = default) => Run(async () => { await tasks.MarkHandled(taskId, throughEventId, ct); return "ok"; });

    [McpServerTool(Name = "add_subtask"), Description("Add a checklist line inside a task (not a card). Assignee may be a person or agent name; a subtask assigned to the user wakes them, one assigned to an agent wakes that agent. Pass requestKey to make a retry safe.")]
    public Task<SubtaskDto> AddSubtask(int taskId, string title, string? assignee = null, int? beforeId = null, int? afterId = null, string? requestKey = null, CancellationToken ct = default) =>
        Run(() => tasks.AddSubtask(taskId, new AddSubtaskRequest(title, assignee, beforeId, afterId, requestKey), ct));

    [McpServerTool(Name = "update_subtask"), Description("Change a subtask's title, done flag and/or assignee ('none' unassigns).")]
    public Task<SubtaskDto> UpdateSubtask(int taskId, int subtaskId, string? title = null, bool? done = null, string? assignee = null, CancellationToken ct = default) =>
        Run(() => tasks.UpdateSubtaskAsync(taskId, subtaskId, new UpdateSubtaskRequest(title, done, assignee), ct));

    [McpServerTool(Name = "claim_task"), Description("Claim an unclaimed pool ('any') task for 15 minutes. Only one agent wins; the loser gets a conflict error and must leave the task alone. Renew with renew_claim while working; the claim holder becomes the assignee when it moves the task to Doing.")]
    public Task<TaskSummaryDto> ClaimTask(int taskId, CancellationToken ct) => Run(() => tasks.ClaimAsync(taskId, ct));

    [McpServerTool(Name = "renew_claim"), Description("Extend your claim on a pool task by another 15 minutes. Only the current holder can renew.")]
    public Task<TaskSummaryDto> RenewClaim(int taskId, CancellationToken ct) => Run(() => tasks.RenewClaimAsync(taskId, ct));

    [McpServerTool(Name = "release_task"), Description("Give up your claim so another agent can take the pool task. Do this when you cannot finish it.")]
    public Task<TaskSummaryDto> ReleaseTask(int taskId, CancellationToken ct) => Run(() => tasks.ReleaseAsync(taskId, ct));
}

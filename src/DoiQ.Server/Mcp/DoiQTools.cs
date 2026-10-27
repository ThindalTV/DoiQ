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
public sealed class DoiQTools(TaskService tasks, BoardService boards, AgentAdminService agents, ICurrentActor current)
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

    [McpServerTool(Name = "list_boards", ReadOnly = true), Description("The boards, each with id, name and how many tasks are open (not Done) and done. A task is on one board at a time; move_task_to_board puts it on another. Your inbox spans every board; every task you get back says which board it is on (boardId, boardName). You cannot add, rename or delete boards: only the user can.")]
    public Task<IReadOnlyList<BoardDto>> ListBoards(CancellationToken ct) => Run(() => boards.ListAsync(ct));

    [McpServerTool(Name = "get_inbox", ReadOnly = true), Description("Your queue across all boards (each item's task has boardId and boardName), oldest first. Each item has a reason: " +
        "'Mentioned' = someone @mentioned you in a comment to ask for your input: read the task (get_task), answer with add_comment or upsert_field, and leave the rest of the task alone (it is not yours unless it is assigned to you). " +
        "'Triage' = a New task to triage (triage agent only). 'Assigned' / 'Subtask' = work assigned to you that you have not handled, or that the user or another agent touched since. " +
        "'Dispatch' = an 'any agent' task for you to pass on (dispatcher only): do no work on it; call list_actors, pick the agent whose responsibilities fit best (yourself included) and assign_task it to them by name; if you picked yourself, carry on with it as your own task. If no agent fits (only the user can do it, or it is unclear what is wanted), call ask_user saying why: that hands it to the user. Never leave it in 'any agent' after you have decided. " +
        "'Pool' = an unclaimed 'any agent' task (only when there is no dispatcher): claim_task it if you can do it. " +
        "A task the user assigns to you (or to 'any agent') from New lands in Waiting and is yours from then on: move it to Doing when you start, do the work, then move it to Done. If you cannot go on without something from the user, call ask_user. " +
        "Work through it, then call mark_handled for each task you acted on or parked so it leaves the queue until someone acts again. Run this whenever you are woken and as a periodic sweep.")]
    public Task<IReadOnlyList<InboxItemDto>> GetInbox(CancellationToken ct) => Run(() => tasks.GetInboxAsync(ct));

    [McpServerTool(Name = "list_actors", ReadOnly = true), Description("Who is on the board: the user and the active agents, each with name and, for agents, their responsibilities (what they do and do not do), isTriage and isDispatcher. Use the names with assign_task, and as @Name in a comment to ask someone for input.")]
    public Task<IReadOnlyList<ActorDto>> ListActors(CancellationToken ct) => Run(() => tasks.GetActorsAsync(ct));

    [McpServerTool(Name = "set_my_responsibilities"), Description("Describe what you are responsible for, in a few sentences: what you do and what you are good at. Describe only what you do: leave out what you cannot do, and do not compare yourself with other agents, including agents that run in the same runner as you. It replaces your current description. The dispatcher reads it to decide which agent gets a task; the user can also edit it in Settings. Call this when a task asks you to describe your responsibilities.")]
    public Task<ActorDto> SetMyResponsibilities(string responsibilities, CancellationToken ct) =>
        Run(() => agents.SetResponsibilities(current.Require().Id, responsibilities, ct));

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

    [McpServerTool(Name = "create_task"), Description("Create a task on a board: pass boardId or boardName (with only one board that is the default; with several and none given you get an error listing them, see list_boards). To put it on another board later, use move_task_to_board. For tasks derived from an email set source='Email' and sourceRef=<message id>: creating the same sourceRef twice returns the existing task (created=false). Pass requestKey to make a retried call safe. Status defaults to New; a task you create goes to the top of its lane as top priority; assignee is a name, 'any' (pool) or omitted. You may only create a task in Doing or Done if you assign it to yourself or to the user.")]
    public Task<CreateTaskResult> CreateTask(string title, string? description = null, string? status = null, string? assignee = null, string? dueDate = null,
        [Description("Initial info fields")] NewFieldDto[]? fields = null, string? source = null, string? sourceRef = null, string? requestKey = null,
        [Description("Board id")] int? boardId = null, [Description("Board name (case-insensitive); boardId wins")] string? boardName = null, CancellationToken ct = default) =>
        Run(() => tasks.Create(new CreateTaskRequest(title, description, string.IsNullOrWhiteSpace(status) ? null : ParseStatus(status), assignee, ParseDue(dueDate), fields,
            ParseSource(source), sourceRef, requestKey, boardId, boardName), ct));

    [McpServerTool(Name = "update_task"), Description("Edit title, description and/or due date (yyyy-MM-dd). Pass expectedVersion (from get_task) to avoid overwriting a concurrent change; on a conflict reload with get_task and retry.")]
    public Task<TaskSummaryDto> UpdateTask(int taskId, string? title = null, string? description = null, string? dueDate = null, bool clearDueDate = false, long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.UpdateAsync(taskId, new UpdateTaskRequest(title, description, ParseDue(dueDate), clearDueDate, expectedVersion), ct));

    [McpServerTool(Name = "move_task"), Description("Change column (New, Waiting, Doing, Done) and/or position: beforeTaskId places it directly above that task, afterTaskId directly below. Done is reached only from Doing: move the task to Doing, do the work, then move it to Done, even when the work is instant. You may move tasks assigned to you and tasks assigned to the user (the user often works a task through you: keep its status current for them; it stays assigned to the user). A pool or unassigned task goes to Doing only while you hold its claim, and to Done only once it is assigned to you. Tasks another agent holds you cannot move: add_comment instead.")]
    public Task<TaskSummaryDto> MoveTask(int taskId, string status, int? beforeTaskId = null, int? afterTaskId = null, long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.Move(taskId, new MoveTaskRequest(ParseStatus(status), beforeTaskId, afterTaskId, expectedVersion), ct));

    [McpServerTool(Name = "move_task_to_board"), Description("Put a task on another board (see list_boards): pass boardId or boardName. It keeps its column, assignee, fields, comments, subtasks and history, and goes to the bottom of its column there. Use it when a task belongs on another board, for example when triaging. You may move the tasks you may edit (not those another agent holds). A task made from a source (sourceRef) cannot go to a board that already has a task from the same source. Pass expectedVersion (from get_task) to avoid overwriting a concurrent change.")]
    public Task<TaskSummaryDto> MoveTaskToBoard(int taskId, [Description("Board id")] int? boardId = null, [Description("Board name (case-insensitive); boardId wins")] string? boardName = null,
        long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.MoveToBoard(taskId, new MoveToBoardRequest(boardId, boardName, expectedVersion), ct));

    [McpServerTool(Name = "assign_task"), Description("Assign to a person or agent by name (e.g. 'User', 'Hermes'), to 'any' (the agent pool, or the dispatcher when there is one), or 'none' to unassign. Assigning a task to another agent wakes it; assigning it to the user notifies them, so do that when you have finished work they should review. To only ask another agent something without handing the task over, @mention it in add_comment instead. When you need information or a decision from them, use ask_user instead, so your question is recorded on the task.")]
    public Task<TaskSummaryDto> AssignTask(int taskId, string assignee, long? expectedVersion = null, CancellationToken ct = default) =>
        Run(() => tasks.AssignAsync(taskId, new AssignRequest(assignee, expectedVersion), ct));

    [McpServerTool(Name = "ask_user"), Description("Hand a task back to the user because you need something from them to go on. question says exactly what you need (markdown): it is posted as your comment, the task is assigned to the user and moved to Waiting, and they are notified. Then call mark_handled and leave the task alone: the user answers in the thread and assigns it back to you. Pass requestKey to make a retry safe.")]
    public Task<TaskSummaryDto> AskUser(int taskId, string question, string? requestKey = null, CancellationToken ct = default) =>
        Run(() => tasks.AskUser(taskId, new AskUserRequest(question, requestKey), ct));

    [McpServerTool(Name = "upsert_field"), Description("Add or update an info field by label (e.g. 'Email summary', 'Research', 'Result'). Re-running a step updates the same field instead of adding duplicates. Value is markdown. Allowed on a task another agent holds while a comment there has @mentioned you for input (your field or comment answers it).")]
    public Task<InfoFieldDto> UpsertField(int taskId, string label, string value, CancellationToken ct) => Run(() => tasks.UpsertFieldAsync(taskId, new FieldRequest(label, value), ct));

    [McpServerTool(Name = "add_comment"), Description("Post to the task's discussion thread (markdown). Allowed on every task, including tasks another agent holds: there it is the only thing you can do, so use it to ask questions or report. " +
        "Write @Name (an agent's or the user's name, see list_actors) to ask them for input without handing the task over: a mentioned agent is woken and answers in the thread or a field; the user is notified. " +
        "Agents may @mention other agents only a few times per task (5 by default) until the user comments again; past that the comment is refused, so drop the @mention or use ask_user. Posting a comment also answers any @mention of you on the task. Pass requestKey to make a retry safe.")]
    public Task<CommentDto> AddComment(int taskId, string body, string? requestKey = null, CancellationToken ct = default) => Run(() => tasks.AddComment(taskId, new AddCommentRequest(body, requestKey), ct));

    [McpServerTool(Name = "mark_seen"), Description("Move your SeenThroughEventId on a task (throughEventId defaults to the latest event). Writes never imply seen.")]
    public Task<string> MarkSeen(int taskId, long? throughEventId = null, CancellationToken ct = default) => Run(async () => { await tasks.MarkSeen(taskId, throughEventId, ct); return "ok"; });

    [McpServerTool(Name = "mark_handled"), Description("Record that you acted on or parked this task up to throughEventId (defaults to the latest event). It leaves your inbox until the user acts on it again. Call it after you finish a task from get_inbox, also when you decided to do nothing. It also closes any @mention of you on the task up to that event.")]
    public Task<string> MarkHandled(int taskId, long? throughEventId = null, CancellationToken ct = default) => Run(async () => { await tasks.MarkHandled(taskId, throughEventId, ct); return "ok"; });

    [McpServerTool(Name = "add_subtask"), Description("Add a checklist line inside a task (not a card). Assignee may be a person or agent name; a subtask assigned to the user wakes them, one assigned to an agent wakes that agent. Pass requestKey to make a retry safe.")]
    public Task<SubtaskDto> AddSubtask(int taskId, string title, string? assignee = null, int? beforeId = null, int? afterId = null, string? requestKey = null, CancellationToken ct = default) =>
        Run(() => tasks.AddSubtask(taskId, new AddSubtaskRequest(title, assignee, beforeId, afterId, requestKey), ct));

    [McpServerTool(Name = "update_subtask"), Description("Change a subtask's title, done flag and/or assignee ('none' unassigns).")]
    public Task<SubtaskDto> UpdateSubtask(int taskId, int subtaskId, string? title = null, bool? done = null, string? assignee = null, CancellationToken ct = default) =>
        Run(() => tasks.UpdateSubtaskAsync(taskId, subtaskId, new UpdateSubtaskRequest(title, done, assignee), ct));

    [McpServerTool(Name = "claim_task"), Description("Claim an unclaimed pool ('any') task for 15 minutes. When there is a dispatcher, only it may claim: other agents wait for it to assign the task. Only one agent wins; the loser gets a conflict error and must leave the task alone. Renew with renew_claim while working; the claim holder becomes the assignee when it moves the task to Doing.")]
    public Task<TaskSummaryDto> ClaimTask(int taskId, CancellationToken ct) => Run(() => tasks.ClaimAsync(taskId, ct));

    [McpServerTool(Name = "renew_claim"), Description("Extend your claim on a pool task by another 15 minutes. Only the current holder can renew.")]
    public Task<TaskSummaryDto> RenewClaim(int taskId, CancellationToken ct) => Run(() => tasks.RenewClaimAsync(taskId, ct));

    [McpServerTool(Name = "release_task"), Description("Give up your claim so another agent can take the pool task. Do this when you cannot finish it.")]
    public Task<TaskSummaryDto> ReleaseTask(int taskId, CancellationToken ct) => Run(() => tasks.ReleaseAsync(taskId, ct));
}

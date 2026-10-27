using System.Text.Json;
using DoiQ.Core.Data;
using DoiQ.Core.Domain;
using DoiQ.Core.Options;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoiQ.Core.Services;

/// <summary>
/// The single write path. REST, MCP and the hub are thin adapters over this class. Every write records an
/// ActivityEvent, queues outbox rows in the same SaveChanges, and broadcasts only after the save succeeded.
/// </summary>
public sealed class TaskService(
    DoiQDbContext db,
    ICurrentActor current,
    IBoardBroadcaster broadcaster,
    IOutboxSignal outboxSignal,
    TimeProvider time,
    IOptions<BoardOptions> boardOptions,
    ILogger<TaskService> logger)
{
    private readonly BoardOptions _board = boardOptions.Value;
    private readonly List<Func<Task>> _after = [];
    private Dictionary<int, Actor>? _actors;
    private Dictionary<int, Board>? _boards;
    private bool _outboxDirty;

    private DateTime Now => time.GetUtcNow().UtcDateTime;
    private ActorInfo Me => current.Require();

    // ───────────────────────── queries ─────────────────────────

    /// <summary>Every matching task, sorted (the REST list). Agents page through <see cref="Search"/> instead.</summary>
    public async Task<IReadOnlyList<TaskSummaryDto>> List(TaskQuery q, CancellationToken ct)
    {
        var me = Me;
        if (await FilterAsync(q, me, ct) is not { } query) return [];
        var tasks = await query.ToListAsync(ct);
        var dtos = await ProjectAsync(tasks, me, ct);
        if (q.NeedsMe) dtos = dtos.Where(d => d.NeedsMe).ToList();
        var comparer = SortComparer.For(q.Sort, q.Dir);
        return dtos.OrderBy(KeyOf, comparer).ToList();
    }

    /// <summary>
    /// One page of <see cref="List"/> (NeedsMe is not applied). The order is computed over light key rows and the cursor
    /// remembers the last key returned (keyset), so a task added or moved meanwhile neither repeats nor shifts the next page.
    /// Only the page itself is projected.
    /// </summary>
    public async Task<TaskPageDto> Search(TaskQuery q, string? cursor, int limit, CancellationToken ct)
    {
        var me = Me;
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var comparer = SortComparer.For(q.Sort, q.Dir);
        var after = SearchCursor.Decode(cursor, comparer);
        if (await FilterAsync(q, me, ct) is not { } query) return new TaskPageDto([], null);
        var keys = await query.Select(t => new SortKey(t.Id, t.Status, t.SortOrder, t.LastActivityAt, t.CreatedAt, t.DueDate, t.Title)).ToListAsync(ct);
        IEnumerable<SortKey> ordered = keys.Order(comparer);
        if (after is not null) ordered = ordered.Where(k => comparer.Compare(k, after) > 0);
        var page = ordered.Take(limit + 1).ToList();
        var more = page.Count > limit;
        if (more) page.RemoveAt(limit);
        return new TaskPageDto(await ProjectInOrderAsync(page.Select(k => k.Id).ToList(), me, ct), more ? SearchCursor.Encode(page[^1], comparer) : null);
    }

    /// <summary>The filtered (unsorted) tasks of a query, or null when it names an assignee that does not exist.</summary>
    private async Task<IQueryable<TaskItem>?> FilterAsync(TaskQuery q, ActorInfo me, CancellationToken ct)
    {
        var board = await ResolveBoardAsync(q.BoardId, q.BoardName, ct);
        IQueryable<TaskItem> query = db.Tasks.AsNoTracking().Where(t => t.BoardId == board.Id);
        if (q.IncludeArchived) query = query.IgnoreQueryFilters();
        if (q.Status is { } st)
        {
            EnumInput.Require(st, "status");
            query = query.Where(t => t.Status == st);
        }

        if (!string.IsNullOrWhiteSpace(q.Assignee))
        {
            var a = q.Assignee.Trim().ToLowerInvariant();
            var actors = await ActorsAsync(ct);
            switch (a)
            {
                case "me": query = query.Where(t => t.AssigneeId == me.Id); break;
                case "any": query = query.Where(t => t.AssignedToPool); break;
                case "none": case "unassigned": query = query.Where(t => t.AssigneeId == null && !t.AssignedToPool); break;
                case "agents":
                    var agentIds = actors.Values.Where(x => x.Kind == ActorKind.Agent).Select(x => x.Id).ToList();
                    query = query.Where(t => t.AssigneeId != null && agentIds.Contains(t.AssigneeId.Value));
                    break;
                default:
                    var hit = actors.Values.FirstOrDefault(x => x.Name.Equals(q.Assignee.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (hit is null) return null;
                    var hitId = hit.Id;
                    query = query.Where(t => t.AssigneeId == hitId);
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(q.Q) ? query : Matching(query, q.Q);
    }

    /// <summary>Tasks whose title, description, info field (label or value), comment or checklist line contains the text (case-insensitive).</summary>
    private IQueryable<TaskItem> Matching(IQueryable<TaskItem> query, string text)
    {
        var pattern = "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return query.Where(t =>
            EF.Functions.Like(t.Title, pattern, "\\") ||
            EF.Functions.Like(t.Description, pattern, "\\") ||
            db.Fields.Any(f => f.TaskId == t.Id && (EF.Functions.Like(f.Value, pattern, "\\") || EF.Functions.Like(f.Label, pattern, "\\"))) ||
            db.Comments.Any(c => c.TaskId == t.Id && EF.Functions.Like(c.Body, pattern, "\\")) ||
            db.Subtasks.Any(s => s.TaskId == t.Id && EF.Functions.Like(s.Title, pattern, "\\")));
    }

    private static SortKey KeyOf(TaskSummaryDto t) => new(t.Id, t.Status, t.SortOrder, t.LastActivityAt, t.CreatedAt, t.DueDate, t.Title);

    /// <summary>Projects the tasks with these ids, in this order (archived ones included).</summary>
    private async Task<List<TaskSummaryDto>> ProjectInOrderAsync(List<int> ids, ActorInfo me, CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking().IgnoreQueryFilters().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var position = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return await ProjectAsync(tasks.OrderBy(t => position[t.Id]).ToList(), me, ct);
    }

    // ───────────────────────── board load and the archive ─────────────────────────

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// The board view in one call and a handful of queries: the board's active tasks with their counts (no description
    /// or lists: the drawer loads those on open), how many tasks are archived, and the event id to catch up from.
    /// </summary>
    public async Task<BoardLoadDto> LoadBoard(int boardId, CancellationToken ct)
    {
        var me = Me;
        var board = await ResolveBoardAsync(boardId, null, ct);
        // Read before the tasks: whatever commits in between is replayed by the client's catch-up from here.
        var latest = await db.Events.AsNoTracking().Select(e => (long?)e.Id).MaxAsync(ct) ?? 0;
        var max = Math.Max(1, _board.MaxBoardTasks);
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.BoardId == board.Id)   // the archive filter applies
            .OrderBy(t => t.Status == BoardStatus.Done).ThenByDescending(t => t.LastActivityAt).ThenBy(t => t.Id)
            .Take(max + 1).ToListAsync(ct);
        var truncated = tasks.Count > max;
        if (truncated)
        {
            tasks.RemoveAt(max);
            logger.LogWarning("Board {BoardId} has more than {Max} active tasks; the board load left the rest out", board.Id, max);
        }
        var archived = await ArchivedOf(board.Id).CountAsync(ct);
        return new BoardLoadDto(board.Id, latest, await ProjectAsync(tasks, me, ct), archived, _board.ArchiveAfterDays, truncated);
    }

    /// <summary>
    /// The board's archived tasks, most recently done first, optionally only those matching <paramref name="q"/>.
    /// Keyset paging on (DoneAt, Id): the cursor is the last task returned, so pages stay stable while tasks are archived or reopened.
    /// </summary>
    public async Task<TaskPageDto> GetArchive(int boardId, string? q, string? cursor, int limit, CancellationToken ct)
    {
        var me = Me;
        var board = await ResolveBoardAsync(boardId, null, ct);
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var query = ArchivedOf(board.Id);
        if (!string.IsNullOrWhiteSpace(q)) query = Matching(query, q);
        if (ArchiveCursor.Decode(cursor) is { } after)
        {
            var (doneAt, id) = after;
            query = query.Where(t => t.DoneAt < doneAt || (t.DoneAt == doneAt && t.Id < id));
        }
        var rows = await query.OrderByDescending(t => t.DoneAt).ThenByDescending(t => t.Id).Take(limit + 1).ToListAsync(ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new TaskPageDto(await ProjectAsync(rows, me, ct), more ? ArchiveCursor.Encode(rows[^1].DoneAt!.Value, rows[^1].Id) : null);
    }

    /// <summary>The board-wide search: ids of the matching active tasks, and the first <paramref name="archivedLimit"/> archived matches.</summary>
    public async Task<BoardSearchDto> SearchBoard(int boardId, string? q, int archivedLimit, CancellationToken ct)
    {
        _ = Me;
        if (string.IsNullOrWhiteSpace(q)) throw new ValidationException("q is required.");
        var board = await ResolveBoardAsync(boardId, null, ct);
        var active = await Matching(db.Tasks.AsNoTracking().Where(t => t.BoardId == board.Id), q).Select(t => t.Id).ToListAsync(ct);
        return new BoardSearchDto(active, await GetArchive(board.Id, q, null, archivedLimit, ct));
    }

    /// <summary>Done for longer than the archive window (exactly what the archive query filter hides).</summary>
    private IQueryable<TaskItem> ArchivedOf(int boardId)
    {
        var cutoff = db.ArchiveCutoff;
        return db.Tasks.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.BoardId == boardId && t.Status == BoardStatus.Done && t.DoneAt != null && t.DoneAt < cutoff);
    }

    public async Task<TaskDetailDto> GetAsync(int id, CancellationToken ct = default)
    {
        var me = Me;
        var t = await db.Tasks.AsNoTracking().IgnoreQueryFilters()
            .Include(x => x.Fields).Include(x => x.Comments).Include(x => x.Subtasks)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException($"Task {id} not found.");
        return await DetailAsync(t, me, ct);
    }

    private async Task<TaskDetailDto> DetailAsync(TaskItem t, ActorInfo? viewer, CancellationToken ct)
    {
        var actors = await ActorsAsync(ct);
        var summary = (await ProjectAsync([t], viewer, ct))[0];
        var latest = await db.Events.Where(e => e.TaskId == t.Id).Select(e => (long?)e.Id).MaxAsync(ct) ?? 0;
        var markers = await db.ReadMarkers.AsNoTracking().Where(m => m.TaskId == t.Id).ToListAsync(ct);
        var mySeen = viewer is null ? 0 : markers.FirstOrDefault(m => m.ActorId == viewer.Id)?.SeenThroughEventId ?? 0;
        var seenBy = markers.Where(m => m.ActorId != viewer?.Id && m.SeenThroughEventId > 0 && actors.TryGetValue(m.ActorId, out var a) && a.IsActive)
            .OrderBy(m => m.ActorId)
            .Select(m => new SeenByDto(m.ActorId, Name(actors, m.ActorId), actors[m.ActorId].Kind, m.SeenThroughEventId)).ToList();
        var commentEvents = await CommentEventIdsAsync(t.Id, ct);
        var mentions = await MentionsOfAsync(t.Id, ct);
        return new TaskDetailDto(
            summary, t.Description, Name(actors, t.CreatedById),
            t.Fields.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).Select(f => ToDto(f, actors)).ToList(),
            t.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => ToDto(c, actors, commentEvents.GetValueOrDefault(c.Id), viewer, mySeen, mentions[c.Id])).ToList(),
            t.Subtasks.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).Select(s => ToDto(s, actors)).ToList(),
            latest, mySeen, seenBy);
    }

    public async Task<IReadOnlyList<CommentDto>> GetCommentsAsync(int taskId, CancellationToken ct = default)
    {
        var me = Me;
        await EnsureExistsAsync(taskId, ct);
        var actors = await ActorsAsync(ct);
        var list = await db.Comments.AsNoTracking().Where(c => c.TaskId == taskId).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var events = await CommentEventIdsAsync(taskId, ct);
        var seen = (await db.ReadMarkers.AsNoTracking().FirstOrDefaultAsync(m => m.TaskId == taskId && m.ActorId == me.Id, ct))?.SeenThroughEventId ?? 0;
        var mentions = await MentionsOfAsync(taskId, ct);
        return list.Select(c => ToDto(c, actors, events.GetValueOrDefault(c.Id), me, seen, mentions[c.Id])).ToList();
    }

    /// <summary>Comment id to the actors that comment @mentioned, each with whether it has answered.</summary>
    private async Task<ILookup<int, MentionDto>> MentionsOfAsync(int taskId, CancellationToken ct)
    {
        var actors = await ActorsAsync(ct);
        return (await db.Mentions.AsNoTracking().Where(m => m.TaskId == taskId).OrderBy(m => m.Id).ToListAsync(ct))
            .ToLookup(m => m.CommentId, m => new MentionDto(m.MentionedId, Name(actors, m.MentionedId), m.AnsweredAt is not null));
    }

    /// <summary>Comment id to the id of the CommentAdded event that created it (read out of the event payloads).</summary>
    private async Task<Dictionary<int, long>> CommentEventIdsAsync(int taskId, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking().Where(e => e.TaskId == taskId && e.Type == EventType.CommentAdded)
            .Select(e => new { e.Id, e.PayloadJson }).ToListAsync(ct);
        var map = new Dictionary<int, long>();
        foreach (var r in rows)
        {
            if (r.PayloadJson is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(r.PayloadJson);
                if (doc.RootElement.TryGetProperty("commentId", out var cid) && cid.TryGetInt32(out var id)) map[id] = r.Id;
            }
            catch (JsonException) { /* not a comment payload we wrote */ }
        }
        return map;
    }

    public async Task<IReadOnlyList<ActorDto>> GetActorsAsync(CancellationToken ct = default) =>
        (await ActorsAsync(ct)).Values.Where(a => a.IsActive).OrderBy(a => a.Id)
            .Select(a => new ActorDto(a.Id, a.Kind, a.Name, a.Initials, a.IsTriage, a.IsDispatcher, a.Responsibilities)).ToList();

    // ───────────────────────── create / update / move / assign / delete ─────────────────────────

    public async Task<CreateTaskResult> Create(CreateTaskRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var title = req.Title?.Trim();
        if (string.IsNullOrEmpty(title)) throw new ValidationException("Title is required.");

        var board = await ResolveBoardAsync(req.BoardId, req.BoardName, ct);

        if (await FindKeyAsync("task", req.RequestKey, ct) is { } prior)
            return new CreateTaskResult(await GetAsync(prior, ct), false);

        // Stored trimmed, so looked up trimmed: " <id>" and "<id>" are the same email.
        var sourceRef = string.IsNullOrWhiteSpace(req.SourceRef) ? null : req.SourceRef.Trim();
        if (sourceRef is not null)
        {
            var existing = await db.Tasks.IgnoreQueryFilters().AsNoTracking().Where(t => t.BoardId == board.Id && t.SourceRef == sourceRef).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
            if (existing is { } eid) return new CreateTaskResult(await GetAsync(eid, ct), false);
        }

        var status = EnumInput.Require(req.Status ?? BoardStatus.New, "status");
        var source = EnumInput.Require(req.Source ?? TaskSource.Manual, "source");
        var (assigneeId, pool) = await ResolveAssigneeAsync(req.Assignee, allowPool: true, ct);
        var actors = await ActorsAsync(ct);
        if (HandsToAgents(me, status, assigneeId, pool)) status = BoardStatus.Waiting;
        var forUser = assigneeId is { } aid && actors.GetValueOrDefault(aid)?.Kind == ActorKind.Human;
        if (me.IsAgent && status is BoardStatus.Doing or BoardStatus.Done && assigneeId != me.Id && !forUser)
            throw new ForbiddenException("Agents may only create tasks in Doing or Done when assigned to themselves or to the user.");

        var now = Now;
        var task = new TaskItem
        {
            BoardId = board.Id,
            Title = title,
            Description = req.Description ?? "",
            Status = status,
            AssigneeId = assigneeId,
            AssignedToPool = pool,
            DueDate = req.DueDate,
            CreatedById = me.Id,
            CreatedAt = now,
            UpdatedAt = now,
            DoneAt = status == BoardStatus.Done ? now : null,
            // An agent's new task is top priority, at the top of its lane; the user's goes to the bottom.
            SortOrder = me.IsAgent ? await TopOrderAsync(board.Id, status, ct) : await BottomOrderAsync(board.Id, status, ct),
            Source = source,
            SourceRef = sourceRef,
            LastActivityAt = now,
            LastActivityById = me.Id,
            Version = 1,
        };
        foreach (var f in req.Fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(f.Label)) throw new ValidationException("Field label is required.");
            if (task.Fields.Any(x => x.Label.Equals(f.Label.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            task.Fields.Add(new InfoField { Label = f.Label.Trim(), Value = f.Value ?? "", AuthorId = me.Id, CreatedAt = now, UpdatedAt = now });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Tasks.Add(task);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (task.SourceRef is not null)
        {
            // lost a race on the unique SourceRef: return the winner
            db.ChangeTracker.Clear();
            var winner = await db.Tasks.IgnoreQueryFilters().AsNoTracking().Where(t => t.BoardId == board.Id && t.SourceRef == sourceRef).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
            if (winner is { } wid) return new CreateTaskResult(await GetAsync(wid, ct), false);
            throw;
        }

        var ev = Record(task, EventType.Created, new { title, status = status.ToString(), assignee = req.Assignee, source = task.Source.ToString() });
        RememberKey("task", req.RequestKey, task.Id);
        await PlanWakeupsAsync(ev, task, me, [], ct);
        try
        {
            await CommitAsync(ct, tx);
        }
        catch (DbUpdateException ex) when (!string.IsNullOrWhiteSpace(req.RequestKey))
        {
            // lost a race on the unique (ActorId, Key): the other call's task is the answer
            if (await RecoverKeyRace("task", req.RequestKey, tx, ct) is not { } winner) throw;
            logger.LogDebug(ex, "Create with requestKey lost the race; returning the stored result");
            return new CreateTaskResult(await GetAsync(winner, ct), false);
        }
        After(() => PublishChangedAsync(ev, task));
        await RunAfterAsync();
        return new CreateTaskResult(await GetAsync(task.Id, ct), true);
    }

    public const string ResponsibilitiesTaskTitle = "Describe your responsibilities";

    /// <summary>
    /// The user asks an agent to describe what it does (from Settings, when adding it or later). DoiQ can only reach an
    /// agent through its board, so this is a task on the first board, assigned to the agent: it arrives like any other
    /// work (webhook, or the next inbox sweep once the agent is connected) and tells it to call set_my_responsibilities.
    /// </summary>
    public async Task<CreateTaskResult> AskForResponsibilities(int agentId, CancellationToken ct)
    {
        var me = Me;
        if (!me.IsHuman) throw new ForbiddenException("Only the user asks an agent to describe itself.");
        var agent = (await ActorsAsync(ct)).GetValueOrDefault(agentId) is { Kind: ActorKind.Agent, IsActive: true } a
            ? a : throw new NotFoundException($"There is no active agent {agentId}.");
        var board = (await BoardsAsync(ct)).Values.OrderBy(b => b.Id).First();
        const string brief = """
            The user is setting up who does what on DoiQ and wants to know what you take care of.

            1. Write a few sentences on your responsibilities: what you do and what you are good at. Describe only what you do:
               leave out what you cannot do, and do not compare yourself with other agents, including agents that run in the same runner as you.
            2. Save them with `set_my_responsibilities` (REST: `PUT /api/me/responsibilities` with `{ "responsibilities": "..." }`). This replaces what is there.
            3. Move this task to Doing, then to Done.

            The dispatcher reads every agent's responsibilities when it decides who gets an "any agent" task, and the user can edit yours in Settings.
            """;
        return await Create(new CreateTaskRequest(ResponsibilitiesTaskTitle, brief, Assignee: agent.Name, BoardId: board.Id), ct);
    }

    public async Task<TaskSummaryDto> UpdateAsync(int id, UpdateTaskRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(id, req.ExpectedVersion, ct);
        EnsureCanWrite(t, me);
        var changed = new List<string>();
        if (req.Title is not null)
        {
            var title = req.Title.Trim();
            if (title.Length == 0) throw new ValidationException("Title cannot be empty.");
            if (title != t.Title) { t.Title = title; changed.Add("title"); }
        }
        if (req.Description is not null && req.Description != t.Description) { t.Description = req.Description; changed.Add("description"); }
        if (req.ClearDueDate && t.DueDate is not null) { t.DueDate = null; changed.Add("dueDate"); }
        else if (req.DueDate is { } due && due != t.DueDate) { t.DueDate = due; changed.Add("dueDate"); }
        if (changed.Count == 0) return (await ProjectAsync([t], me, ct))[0];

        t.UpdatedAt = Now;
        t.Version++;
        var ev = Record(t, EventType.Updated, new { changed });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    public async Task<TaskSummaryDto> Move(int id, MoveTaskRequest req, CancellationToken ct = default)
    {
        var me = Me;
        EnumInput.Require(req.Status, "status");
        var t = await LoadAsync(id, req.ExpectedVersion, ct);
        var from = t.Status;
        var now = Now;

        // Agents may move their own tasks and the user's (the user often works a task through an agent), to any lane;
        // the user's task stays the user's. Pool and unassigned tasks reach Doing through a claim, Done only once assigned.
        if (me.IsAgent)
        {
            EnsureCanWrite(t, me);
            var forUser = AssignedToHuman(t);
            if (req.Status == BoardStatus.Done && t.AssigneeId != me.Id && !forUser)
                throw new ForbiddenException("Agents can only move tasks assigned to them or to the user to Done. Claim a pool task and move it to Doing first.");
            // Done is always reached through Doing, even when the work itself takes no time.
            if (req.Status == BoardStatus.Done && from is not (BoardStatus.Doing or BoardStatus.Done))
                throw new ForbiddenException($"Agents move a task to Done only from Doing, and this one is in {from}. Move it to Doing, do the work, then move it to Done.");
            if (req.Status == BoardStatus.Doing && !forUser)
            {
                var holdsClaim = t.ClaimedById == me.Id && ClaimLive(t, now);
                if (t.AssigneeId != me.Id && !holdsClaim)
                    throw new ForbiddenException("Only the assignee, or the agent holding a live claim, can move a task to Doing.");
                if (t.AssigneeId != me.Id)
                {
                    t.AssigneeId = me.Id;
                    t.AssignedToPool = false;
                }
                t.ClaimedById = null;
                t.ClaimExpiresAt = null;
            }
        }

        if (req.Status == BoardStatus.Done) t.DoneAt = from == BoardStatus.Done ? t.DoneAt : now;
        else t.DoneAt = null;
        t.Status = req.Status;
        t.SortOrder = await ComputeSortOrderAsync(t.BoardId, req.Status, t.Id, req.BeforeTaskId, req.AfterTaskId, ct);
        t.UpdatedAt = now;
        t.Version++;

        var ev = Record(t, EventType.Moved, new { from = from.ToString(), to = req.Status.ToString() });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    /// <summary>
    /// Puts a task on another board, in the same lane (at the bottom), with everything it has: fields, comments,
    /// checklist, assignee, claim and history. The user may move any task; an agent what it may edit. The task's
    /// queued webhooks follow it, so they name the new board.
    /// </summary>
    public async Task<TaskSummaryDto> MoveToBoard(int id, MoveToBoardRequest req, CancellationToken ct)
    {
        var me = Me;
        if (req.BoardId is null && string.IsNullOrWhiteSpace(req.BoardName))
            throw new ValidationException("Say which board to move the task to: boardId or boardName (see list_boards).");
        var t = await LoadAsync(id, req.ExpectedVersion, ct);
        EnsureCanWrite(t, me);
        var to = await ResolveBoardAsync(req.BoardId, req.BoardName, ct);
        if (to.Id == t.BoardId) return (await ProjectAsync([t], me, ct))[0];
        if (t.SourceRef is { } sourceRef &&
            await db.Tasks.IgnoreQueryFilters().AnyAsync(x => x.BoardId == to.Id && x.SourceRef == sourceRef, ct))
            throw new ConflictException($"Board '{to.Name}' already has a task made from the same source ({sourceRef}).");

        var boards = await BoardsAsync(ct);
        var fromName = BoardNameOf(boards, t.BoardId);
        t.BoardId = to.Id;
        t.SortOrder = await BottomOrderAsyncExcluding(to.Id, t.Status, t.Id, ct);
        t.UpdatedAt = Now;
        t.Version++;
        foreach (var o in await db.Outbox.Where(o => o.TaskId == id && o.SentAt == null).ToListAsync(ct)) o.BoardId = to.Id;

        var ev = Record(t, EventType.MovedToBoard, new { from = fromName, to = to.Name });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    public async Task<TaskSummaryDto> AssignAsync(int id, AssignRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(id, req.ExpectedVersion, ct);
        EnsureCanWrite(t, me);
        var (assigneeId, pool) = await ResolveAssigneeAsync(req.Assignee, allowPool: true, ct);
        var before = DescribeAssignee(await ActorsAsync(ct), t);
        if (HandsToAgents(me, t.Status, assigneeId, pool))
        {
            t.Status = BoardStatus.Waiting;
            t.SortOrder = await ComputeSortOrderAsync(t.BoardId, BoardStatus.Waiting, t.Id, null, null, ct);
            Record(t, EventType.Moved, new { from = BoardStatus.New.ToString(), to = BoardStatus.Waiting.ToString() });
        }
        t.AssigneeId = assigneeId;
        t.AssignedToPool = pool;
        t.ClaimedById = null;
        t.ClaimExpiresAt = null;
        t.UpdatedAt = Now;
        t.Version++;
        var after = DescribeAssignee(await ActorsAsync(ct), t);
        var ev = Record(t, EventType.Assigned, new { from = before, to = after });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    /// <summary>
    /// An agent needs something from the user before it can go on: its question goes into the thread, the task is
    /// assigned to the user and waits in Waiting for them. One commit, so the board never shows half of it.
    /// </summary>
    public async Task<TaskSummaryDto> AskUser(int taskId, AskUserRequest req, CancellationToken ct)
    {
        var me = Me;
        if (!me.IsAgent) throw new ForbiddenException("Only agents ask the user. Assign the task instead.");
        var question = req.Question?.Trim();
        if (string.IsNullOrEmpty(question)) throw new ValidationException("question is required: say what you need from the user.");
        var t = await LoadAsync(taskId, null, ct);
        EnsureCanWrite(t, me);
        if (await FindKeyAsync("comment", req.RequestKey, ct) is not null) return (await ProjectAsync([t], me, ct))[0];
        var actors = await ActorsAsync(ct);
        var user = actors.Values.Where(a => a.Kind == ActorKind.Human && a.IsActive).OrderBy(a => a.Id).FirstOrDefault()
            ?? throw new ValidationException("There is no user to ask.");

        var now = Now;
        var c = new Comment { TaskId = taskId, AuthorId = me.Id, Body = question, CreatedAt = now };
        db.Comments.Add(c);
        var asked = Record(t, EventType.CommentAdded, new { commentId = 0 });
        var from = t.Status;
        if (from != BoardStatus.Waiting)
        {
            t.Status = BoardStatus.Waiting;
            t.DoneAt = null;
            t.SortOrder = await ComputeSortOrderAsync(t.BoardId, BoardStatus.Waiting, t.Id, null, null, ct);
            Record(t, EventType.Moved, new { from = from.ToString(), to = BoardStatus.Waiting.ToString() });
        }
        var before = DescribeAssignee(actors, t);
        t.AssigneeId = user.Id;
        t.AssignedToPool = false;
        t.ClaimedById = null;
        t.ClaimExpiresAt = null;
        t.UpdatedAt = now;
        t.Version++;
        var ev = Record(t, EventType.Assigned, new { from = before, to = user.Name });
        await UpsertOutboxAsync(OutboxKind.Notify, t, me.Id, ev, $"{me.Name} needs your input: {t.Title}", ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CommitAsync(ct, tx, beforeCommit: async () =>
            {
                asked.PayloadJson = JsonSerializer.Serialize(new { commentId = c.Id });
                RememberKey("comment", req.RequestKey, c.Id);
                await db.SaveChangesAsync(ct);
            });
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(req.RequestKey))
        {
            if (await RecoverKeyRace("comment", req.RequestKey, tx, ct) is null) throw;
            return (await ProjectAsync([await LoadAsync(taskId, null, ct)], me, ct))[0];
        }
        var dto = ToDto(c, actors, asked.Id);
        After(() => broadcaster.CommentAddedAsync(new CommentAddedMessage(asked.Id, me.Id, me.Kind, taskId, dto, t.BoardId)));
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var me = Me;
        if (!me.IsHuman) throw new ForbiddenException("Agents cannot delete tasks.");
        var t = await LoadAsync(id, null, ct);
        var ev = Record(t, EventType.Deleted, new { title = t.Title });
        var pendingOutbox = await db.Outbox.Where(o => o.TaskId == id && o.SentAt == null).ToListAsync(ct);
        db.Outbox.RemoveRange(pendingOutbox);
        db.RequestKeys.RemoveRange(await RequestKeysFor(db, db.Tasks.IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.Id)).ToListAsync(ct));
        db.Tasks.Remove(t);
        await CommitAsync(ct);
        After(() => broadcaster.TaskDeletedAsync(new TaskDeletedMessage(ev.Id, me.Id, id, t.BoardId)));
        await RunAfterAsync();
    }

    // ───────────────────────── info fields ─────────────────────────

    public async Task<InfoFieldDto> AddFieldAsync(int taskId, FieldRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        await EnsureCanWriteFieldAsync(t, me, ct);
        var label = RequireLabel(req.Label);
        if (await db.Fields.AnyAsync(f => f.TaskId == taskId && f.Label == label, ct))
            throw new ConflictException($"A field labelled '{label}' already exists.");
        var now = Now;
        var f = new InfoField { TaskId = taskId, Label = label, Value = req.Value ?? "", AuthorId = me.Id, CreatedAt = now, UpdatedAt = now };
        db.Fields.Add(f);
        await AnswerMentionsAsync(taskId, me.Id, null, ct);
        var ev = Record(t, EventType.FieldAdded, new { label });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await CommitAsync(ct);
        return await PublishFieldAsync(ev, f, t.BoardId);
    }

    /// <summary>Add or update by label (idempotent: an identical value writes nothing).</summary>
    public async Task<InfoFieldDto> UpsertFieldAsync(int taskId, FieldRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        await EnsureCanWriteFieldAsync(t, me, ct);
        var label = RequireLabel(req.Label);
        var f = await db.Fields.FirstOrDefaultAsync(x => x.TaskId == taskId && x.Label == label, ct);
        var actors = await ActorsAsync(ct);
        if (f is null) return await AddFieldAsync(taskId, req, ct);
        if (f.Value == (req.Value ?? "")) return ToDto(f, actors);
        return await ApplyFieldUpdateAsync(t, f, null, req.Value ?? "", me, ct);
    }

    public async Task<InfoFieldDto> UpdateFieldAsync(int taskId, int fieldId, FieldRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        await EnsureCanWriteFieldAsync(t, me, ct);
        var f = await db.Fields.FirstOrDefaultAsync(x => x.TaskId == taskId && x.Id == fieldId, ct) ?? throw new NotFoundException("Field not found.");
        var newLabel = string.IsNullOrWhiteSpace(req.Label) ? null : RequireLabel(req.Label);
        if (newLabel is not null && !newLabel.Equals(f.Label, StringComparison.OrdinalIgnoreCase)
            && await db.Fields.AnyAsync(x => x.TaskId == taskId && x.Label == newLabel && x.Id != fieldId, ct))
            throw new ConflictException($"A field labelled '{newLabel}' already exists.");
        return await ApplyFieldUpdateAsync(t, f, newLabel, req.Value ?? f.Value, me, ct);
    }

    private async Task<InfoFieldDto> ApplyFieldUpdateAsync(TaskItem t, InfoField f, string? newLabel, string value, ActorInfo me, CancellationToken ct)
    {
        if (newLabel is not null) f.Label = newLabel;
        f.Value = value;
        f.UpdatedAt = Now;
        await AnswerMentionsAsync(t.Id, me.Id, null, ct);
        var ev = Record(t, EventType.FieldUpdated, new { fieldId = f.Id, label = f.Label });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await CommitAsync(ct);
        return await PublishFieldAsync(ev, f, t.BoardId);
    }

    public async Task DeleteFieldAsync(int taskId, int fieldId, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        EnsureCanWrite(t, me);
        var f = await db.Fields.FirstOrDefaultAsync(x => x.TaskId == taskId && x.Id == fieldId, ct) ?? throw new NotFoundException("Field not found.");
        db.Fields.Remove(f);
        var ev = Record(t, EventType.FieldDeleted, new { fieldId, label = f.Label });
        await CommitAsync(ct);
        After(() => broadcaster.FieldChangedAsync(new FieldChangedMessage(ev.Id, me.Id, me.Kind, taskId, fieldId, null, t.BoardId)));
        await RunAfterAsync();
    }

    private async Task<InfoFieldDto> PublishFieldAsync(ActivityEvent ev, InfoField f, int boardId)
    {
        var me = Me;
        var dto = ToDto(f, await ActorsAsync());
        After(() => broadcaster.FieldChangedAsync(new FieldChangedMessage(ev.Id, me.Id, me.Kind, f.TaskId, f.Id, dto, boardId)));
        await RunAfterAsync();
        return dto;
    }

    // ───────────────────────── comments ─────────────────────────

    public async Task<CommentDto> AddComment(int taskId, AddCommentRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var body = req.Body?.Trim();
        if (string.IsNullOrEmpty(body)) throw new ValidationException("Comment body is required.");
        var t = await LoadAsync(taskId, null, ct);   // any actor may comment, even on tasks another agent holds
        if (await FindKeyAsync("comment", req.RequestKey, ct) is { } prior)
        {
            var c0 = await db.Comments.AsNoTracking().FirstAsync(c => c.Id == prior, ct);
            return ToDto(c0, await ActorsAsync(ct));
        }
        var actors = await ActorsAsync(ct);
        var mentioned = FindMentions(body, actors.Values, me.Id);
        // The loop guard: agents asking agents is capped until the user speaks again.
        if (me.IsHuman) t.AgentMentionHops = 0;
        else if (mentioned.Any(a => a.Kind == ActorKind.Agent))
        {
            if (t.AgentMentionHops >= _board.MaxAgentMentionHops)
                throw new ForbiddenException($"Agents have @mentioned each other {t.AgentMentionHops} times on this task since the user last commented, the most allowed. " +
                    "Post your comment without the @mention, or use ask_user to bring the user in; their next comment lifts the limit.");
            t.AgentMentionHops++;
        }

        var now = Now;
        var c = new Comment { TaskId = taskId, AuthorId = me.Id, Body = body, CreatedAt = now };
        db.Comments.Add(c);
        var asked = mentioned.Select(a => new Mention { TaskId = taskId, Comment = c, MentionedId = a.Id, ById = me.Id, CreatedAt = now }).ToList();
        db.Mentions.AddRange(asked);
        await AnswerMentionsAsync(taskId, me.Id, null, ct);
        var ev = Record(t, EventType.CommentAdded, new { commentId = 0 });
        await PlanWakeupsAsync(ev, t, me, [], ct);
        await PlanMentionWakeupsAsync(ev, t, me, mentioned, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CommitAsync(ct, tx, beforeCommit: async () =>
            {
                ev.PayloadJson = JsonSerializer.Serialize(new { commentId = c.Id });
                foreach (var m in asked) m.EventId = ev.Id;
                RememberKey("comment", req.RequestKey, c.Id);
                await db.SaveChangesAsync(ct);
            });
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(req.RequestKey))
        {
            if (await RecoverKeyRace("comment", req.RequestKey, tx, ct) is not { } winner) throw;
            return ToDto(await db.Comments.AsNoTracking().FirstAsync(x => x.Id == winner, ct), await ActorsAsync(ct));
        }
        var dto = ToDto(c, actors, ev.Id, mentions: asked.Select(m => new MentionDto(m.MentionedId, Name(actors, m.MentionedId), false)));
        After(() => broadcaster.CommentAddedAsync(new CommentAddedMessage(ev.Id, me.Id, me.Kind, taskId, dto, t.BoardId)));
        await RunAfterAsync();
        return dto;
    }

    /// <summary>
    /// The active actors a comment @mentions, its author left out. A mention is "@" plus the actor's name (any case) with
    /// no letter or digit on either side, so "ada@example.com" and "@Adam" do not mention Ada. Longer names are matched
    /// first, so "@Hermes 2" mentions Hermes 2 and not Hermes.
    /// </summary>
    private static List<Actor> FindMentions(string body, IEnumerable<Actor> actors, int authorId)
    {
        var found = new List<Actor>();
        var claimed = new HashSet<int>();   // start positions already read as a (longer) name
        foreach (var a in actors.Where(a => a.IsActive && a.Id != authorId).OrderByDescending(a => a.Name.Length))
        {
            var needle = "@" + a.Name;
            for (var i = body.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0; i = body.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = i + needle.Length;
                if (claimed.Contains(i) || (i > 0 && IsNameChar(body[i - 1])) || (end < body.Length && IsNameChar(body[end]))) continue;
                claimed.Add(i);
                found.Add(a);
                break;
            }
        }
        return found;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The actor has answered what it was asked on this task (up to an event; null = everything so far).</summary>
    private async Task AnswerMentionsAsync(int taskId, int actorId, long? throughEventId, CancellationToken ct)
    {
        var open = await db.Mentions.Where(m => m.TaskId == taskId && m.MentionedId == actorId && m.AnsweredAt == null
            && (throughEventId == null || m.EventId <= throughEventId)).ToListAsync(ct);
        foreach (var m in open) m.AnsweredAt = Now;
    }

    // ───────────────────────── subtasks ─────────────────────────

    public async Task<SubtaskDto> AddSubtask(int taskId, AddSubtaskRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var title = req.Title?.Trim();
        if (string.IsNullOrEmpty(title)) throw new ValidationException("Title is required.");
        var t = await LoadAsync(taskId, null, ct);
        EnsureCanWrite(t, me);
        if (await FindKeyAsync("subtask", req.RequestKey, ct) is { } prior)
            return ToDto(await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == prior, ct), await ActorsAsync(ct));

        var (assigneeId, _) = await ResolveAssigneeAsync(req.Assignee, allowPool: false, ct);
        var s = new Subtask
        {
            TaskId = taskId, Title = title, AssigneeId = assigneeId, CreatedById = me.Id,
            SortOrder = await SubtaskOrderAsync(taskId, 0, req.BeforeId, req.AfterId, ct),
        };
        db.Subtasks.Add(s);
        var ev = Record(t, EventType.SubtaskAdded, new { title });
        await PlanWakeupsAsync(ev, t, me, assigneeId is { } aid ? [aid] : [], ct);
        await NotifySubtaskForUserAsync(ev, t, me, assigneeId, title, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CommitAsync(ct, tx, beforeCommit: async () =>
            {
                ev.PayloadJson = JsonSerializer.Serialize(new { subtaskId = s.Id, title });
                RememberKey("subtask", req.RequestKey, s.Id);
                await db.SaveChangesAsync(ct);
            });
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(req.RequestKey))
        {
            if (await RecoverKeyRace("subtask", req.RequestKey, tx, ct) is not { } winner) throw;
            return ToDto(await db.Subtasks.AsNoTracking().FirstAsync(x => x.Id == winner, ct), await ActorsAsync(ct));
        }
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return ToDto(s, await ActorsAsync(ct));
    }

    public async Task<SubtaskDto> UpdateSubtaskAsync(int taskId, int subtaskId, UpdateSubtaskRequest req, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        EnsureCanWrite(t, me);
        var s = await db.Subtasks.FirstOrDefaultAsync(x => x.TaskId == taskId && x.Id == subtaskId, ct) ?? throw new NotFoundException("Subtask not found.");
        if (req.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Title)) throw new ValidationException("Title cannot be empty.");
            s.Title = req.Title.Trim();
        }
        if (req.Done is { } done && done != s.Done) { s.Done = done; s.DoneAt = done ? Now : null; }
        var assignedBefore = s.AssigneeId;
        if (req.Assignee is not null) (s.AssigneeId, _) = await ResolveAssigneeAsync(req.Assignee, allowPool: false, ct);
        if (req.BeforeId is not null || req.AfterId is not null)
            s.SortOrder = await SubtaskOrderAsync(taskId, subtaskId, req.BeforeId, req.AfterId, ct);

        var ev = Record(t, EventType.SubtaskUpdated, new { subtaskId, title = s.Title, done = s.Done });
        await PlanWakeupsAsync(ev, t, me, s.AssigneeId is { } aid ? [aid] : [], ct);
        if (s.AssigneeId != assignedBefore) await NotifySubtaskForUserAsync(ev, t, me, s.AssigneeId, s.Title, ct);
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return ToDto(s, await ActorsAsync(ct));
    }

    public async Task DeleteSubtaskAsync(int taskId, int subtaskId, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        EnsureCanWrite(t, me);
        var s = await db.Subtasks.FirstOrDefaultAsync(x => x.TaskId == taskId && x.Id == subtaskId, ct) ?? throw new NotFoundException("Subtask not found.");
        db.Subtasks.Remove(s);
        var ev = Record(t, EventType.SubtaskDeleted, new { subtaskId, title = s.Title });
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
    }

    private async Task<double> SubtaskOrderAsync(int taskId, int selfId, int? before, int? after, CancellationToken ct)
    {
        var siblings = await db.Subtasks.AsNoTracking().Where(s => s.TaskId == taskId && s.Id != selfId)
            .Select(s => new { s.Id, s.SortOrder }).ToListAsync(ct);
        // checklist order is ascending: BeforeId = place directly before that line, AfterId = directly after it
        if (before is { } b)
        {
            var x = siblings.FirstOrDefault(s => s.Id == b) ?? throw new ValidationException("beforeId is not a subtask of this task.");
            var prev = siblings.Where(s => s.SortOrder < x.SortOrder).Select(s => (double?)s.SortOrder).Max();
            return prev is null ? x.SortOrder - 1 : (prev.Value + x.SortOrder) / 2;
        }
        if (after is { } a)
        {
            var x = siblings.FirstOrDefault(s => s.Id == a) ?? throw new ValidationException("afterId is not a subtask of this task.");
            var next = siblings.Where(s => s.SortOrder > x.SortOrder).Select(s => (double?)s.SortOrder).Min();
            return next is null ? x.SortOrder + 1 : (next.Value + x.SortOrder) / 2;
        }
        return siblings.Count == 0 ? 0 : siblings.Max(s => s.SortOrder) + 1;
    }

    // ───────────────────────── claims (pool work) ─────────────────────────

    public async Task<TaskSummaryDto> ClaimAsync(int taskId, CancellationToken ct = default)
    {
        var me = Me;
        if (!me.IsAgent) throw new ForbiddenException("Only agents claim pool tasks.");
        if (DispatcherOf(await ActorsAsync(ct)) is { } d && d.Id != me.Id)
            throw new ForbiddenException($"{d.Name} dispatches 'any agent' tasks: leave this one alone, it will assign it to the agent best placed to do it.");
        var now = Now;
        var until = now.AddMinutes(_board.ClaimLeaseMinutes);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One conditional update: only one agent can win.
        var won = await db.Tasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId && t.AssignedToPool && t.Status != BoardStatus.Done && (t.ClaimedById == null || t.ClaimExpiresAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedById, (int?)me.Id).SetProperty(t => t.ClaimExpiresAt, (DateTime?)until).SetProperty(t => t.Version, t => t.Version + 1), ct);
        if (won == 0)
        {
            var t0 = await db.Tasks.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw new NotFoundException($"Task {taskId} not found.");
            if (t0.ClaimedById == me.Id && ClaimLive(t0, now)) return await RenewClaimAsync(taskId, ct);
            if (!t0.AssignedToPool || t0.Status == BoardStatus.Done) throw new ValidationException("Task is not claimable (not in the pool, or already done).");
            throw new ConflictException("Task is already claimed by another agent.");
        }
        db.ChangeTracker.Clear();
        var t = await LoadAsync(taskId, null, ct);
        var ev = Record(t, EventType.Claimed, new { until });
        await CommitAsync(ct, tx);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    public async Task<TaskSummaryDto> RenewClaimAsync(int taskId, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        if (t.ClaimedById != me.Id) throw new ForbiddenException("You do not hold the claim on this task.");
        t.ClaimExpiresAt = Now.AddMinutes(_board.ClaimLeaseMinutes);
        await CommitAsync(ct);
        return (await ProjectAsync([t], me, ct))[0];
    }

    public async Task<TaskSummaryDto> ReleaseAsync(int taskId, CancellationToken ct = default)
    {
        var me = Me;
        var t = await LoadAsync(taskId, null, ct);
        if (t.ClaimedById != me.Id) throw new ForbiddenException("You do not hold the claim on this task.");
        t.ClaimedById = null;
        t.ClaimExpiresAt = null;
        t.Version++;
        var ev = Record(t, EventType.Released, null);
        await CommitAsync(ct);
        After(() => PublishChangedAsync(ev, t));
        await RunAfterAsync();
        return (await ProjectAsync([t], me, ct))[0];
    }

    // ───────────────────────── seen / handled / inbox / events ─────────────────────────

    /// <summary>Moves the caller's SeenThroughEventId (monotonic). Records no event and is never implied by a write.</summary>
    public async Task MarkSeen(int taskId, long? throughEventId, CancellationToken ct = default) =>
        await MoveMarkerAsync(taskId, throughEventId, handled: false, ct);

    /// <summary>Records that the caller acted on or parked the task up to this event.</summary>
    public async Task MarkHandled(int taskId, long? throughEventId, CancellationToken ct = default) =>
        await MoveMarkerAsync(taskId, throughEventId, handled: true, ct);

    private async Task MoveMarkerAsync(int taskId, long? through, bool handled, CancellationToken ct)
    {
        var me = Me;
        var boardId = await BoardOfAsync(taskId, ct);
        var latest = await db.Events.Where(e => e.TaskId == taskId).Select(e => (long?)e.Id).MaxAsync(ct) ?? 0;
        // A position past the task's newest event would hide events that do not exist yet: clamp it.
        var target = Math.Clamp(through ?? latest, 0, latest);
        var m = await db.ReadMarkers.FindAsync([taskId, me.Id], ct);
        if (m is null)
        {
            m = new ReadMarker { TaskId = taskId, ActorId = me.Id };
            db.ReadMarkers.Add(m);
        }
        var before = m.SeenThroughEventId;
        if (handled) await AnswerMentionsAsync(taskId, me.Id, target, ct);   // handled = it decided what to do about being asked, too
        if (handled) m.HandledThroughEventId = Math.Max(m.HandledThroughEventId, target);
        else m.SeenThroughEventId = Math.Max(m.SeenThroughEventId, target);
        await db.SaveChangesAsync(ct);
        if (handled || m.SeenThroughEventId == before) return;
        var seenThrough = m.SeenThroughEventId;
        After(() => broadcaster.SeenChangedAsync(new SeenChangedMessage(taskId, me.Id, me.Kind, me.Name, seenThrough, boardId)));
        await RunAfterAsync();
    }

    public async Task<IReadOnlyList<InboxItemDto>> GetInboxAsync(CancellationToken ct = default)
    {
        var me = Me;
        var now = Now;
        var tasks = await db.Tasks.AsNoTracking().ToListAsync(ct);   // archive filter applies
        var dtos = (await ProjectAsync(tasks, me, ct)).ToDictionary(d => d.Id);
        var latest = await LatestEventsAsync(tasks.Select(t => t.Id).ToList(), ct);
        var result = new List<(TaskItem T, string Reason)>();

        var mentioned = (await db.Mentions.AsNoTracking().Where(m => m.MentionedId == me.Id && m.AnsweredAt == null)
            .Select(m => m.TaskId).Distinct().ToListAsync(ct)).ToHashSet();

        if (me.IsHuman)
        {
            foreach (var t in tasks)
            {
                if (t.AssigneeId == me.Id && t.Status == BoardStatus.Waiting) result.Add((t, "AssignedWaiting"));
                else if (mentioned.Contains(t.Id)) result.Add((t, "Mentioned"));
                else if (dtos[t.Id].Unread) result.Add((t, "Unread"));
            }
        }
        else
        {
            var markers = await db.ReadMarkers.AsNoTracking().Where(m => m.ActorId == me.Id).ToDictionaryAsync(m => m.TaskId, ct);
            var actors = await ActorsAsync(ct);
            var humanIds = actors.Values.Where(a => a.Kind == ActorKind.Human).Select(a => a.Id).ToList();
            var dispatcher = DispatcherOf(actors);
            var ids = tasks.Select(t => t.Id).ToList();
            // What brings a handled task back: anything the user did, or another agent (the triage agent, the dispatcher) assigning it.
            var meId = me.Id;
            var lastPoke = (await db.Events.AsNoTracking()
                    .Where(e => ids.Contains(e.TaskId) && (humanIds.Contains(e.ActorId) || (e.Type == EventType.Assigned && e.ActorId != meId)))
                    .GroupBy(e => e.TaskId).Select(g => new { g.Key, Max = g.Max(e => e.Id) }).ToListAsync(ct))
                .ToDictionary(x => x.Key, x => x.Max);
            var subtaskTasks = (await db.Subtasks.AsNoTracking().Where(s => s.AssigneeId == me.Id && !s.Done).Select(s => s.TaskId).Distinct().ToListAsync(ct)).ToHashSet();

            foreach (var t in tasks)
            {
                // Being asked for input counts even on a Done task: someone wants an answer.
                if (mentioned.Contains(t.Id)) { result.Add((t, "Mentioned")); continue; }
                if (t.Status == BoardStatus.Done) continue;
                var handled = markers.TryGetValue(t.Id, out var mk) ? mk.HandledThroughEventId : 0;
                var needs = handled == 0 || (lastPoke.TryGetValue(t.Id, out var h) && h > handled);
                var open = t.AssignedToPool && !ClaimLive(t, now);
                if (me.IsTriage && t.Status == BoardStatus.New && needs) result.Add((t, "Triage"));
                else if (t.AssigneeId == me.Id && needs) result.Add((t, "Assigned"));
                else if (subtaskTasks.Contains(t.Id) && needs) result.Add((t, "Subtask"));
                else if (open && dispatcher is null) result.Add((t, "Pool"));
                else if (open && dispatcher?.Id == me.Id) result.Add((t, "Dispatch"));
            }
        }

        return result.OrderBy(r => r.T.LastActivityAt).ThenBy(r => r.T.Id)
            .Select(r => new InboxItemDto(dtos[r.T.Id], r.Reason, latest.GetValueOrDefault(r.T.Id))).ToList();
    }

    /// <summary>Events after a cursor. <paramref name="boardId"/> = null spans every board (each event says which one).</summary>
    public async Task<EventFeedDto> GetEventsAsync(long after, int limit = 100, int? boardId = null, CancellationToken ct = default)
    {
        var me = Me;
        limit = Math.Clamp(limit, 1, 500);
        if (boardId is { } scope) await ResolveBoardAsync(scope, null, ct);
        IQueryable<ActivityEvent> q = db.Events.AsNoTracking().Where(e => e.Id > after);
        if (boardId is { } only) q = q.Where(e => e.BoardId == only);
        if (me.IsAgent) q = q.Where(e => e.ActorId != me.Id);   // an agent's feed leaves out its own changes
        var rows = await q.OrderBy(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var actors = await ActorsAsync(ct);
        var boards = await BoardsAsync(ct);
        var events = rows.Select(e => new EventDto(e.Id, e.TaskId, e.Type, e.ActorId, Name(actors, e.ActorId),
            actors.TryGetValue(e.ActorId, out var a) ? a.Kind : ActorKind.Human, e.At, e.PayloadJson, e.BoardId, BoardNameOf(boards, e.BoardId))).ToList();
        return new EventFeedDto(events, events.Count > 0 ? events[^1].Id : after, hasMore);
    }

    /// <summary>The newest event id (0 when there are none): a client records it before loading, then catches up from it.</summary>
    public async Task<long> GetLatestEventIdAsync(int? boardId = null, CancellationToken ct = default)
    {
        _ = Me;
        IQueryable<ActivityEvent> q = db.Events.AsNoTracking();
        if (boardId is { } only) q = q.Where(e => e.BoardId == only);
        return await q.Select(e => (long?)e.Id).MaxAsync(ct) ?? 0;
    }

    // ───────────────────────── internals ─────────────────────────

    private static bool ClaimLive(TaskItem t, DateTime now) => t.ClaimedById is not null && t.ClaimExpiresAt > now;

    private async Task<TaskItem> LoadAsync(int id, long? expectedVersion, CancellationToken ct)
    {
        await ActorsAsync(ct);   // EnsureCanWrite needs the actor cache
        var t = await db.Tasks.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException($"Task {id} not found.");
        if (expectedVersion is { } v && v != t.Version)
            throw new StaleVersionException(id, t.Version, v, Name(_actors!, t.LastActivityById));
        return t;
    }

    private async Task EnsureExistsAsync(int id, CancellationToken ct) => await BoardOfAsync(id, ct);

    /// <summary>The board id of a task (also proves the task exists).</summary>
    private async Task<int> BoardOfAsync(int id, CancellationToken ct) =>
        await db.Tasks.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == id).Select(t => (int?)t.BoardId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Task {id} not found.");

    private async Task<Dictionary<int, Board>> BoardsAsync(CancellationToken ct = default) =>
        _boards ??= await db.Boards.AsNoTracking().ToDictionaryAsync(b => b.Id, ct);

    private static string BoardNameOf(Dictionary<int, Board> boards, int id) => boards.TryGetValue(id, out var b) ? b.Name : "?";

    /// <summary>The id of the board named by id or name (404 if there is none).</summary>
    public async Task<int> FindBoardIdAsync(int? id, string? name, CancellationToken ct = default)
    {
        _ = Me;
        return (await ResolveBoardAsync(id, name, ct)).Id;
    }

    private async Task<Board> ResolveBoardAsync(int? id, string? name, CancellationToken ct) =>
        BoardResolver.Pick((await BoardsAsync(ct)).Values.OrderBy(b => b.Id).ToList(), id, name);

    /// <summary>
    /// The user giving a New task to an agent or the pool: it now belongs to the agents and is ready for them, so it goes
    /// to Waiting. An agent assigning (the triage agent passing work on) leaves the lane alone.
    /// </summary>
    private bool HandsToAgents(ActorInfo me, BoardStatus status, int? assigneeId, bool pool) =>
        me.IsHuman && status == BoardStatus.New
        && (pool || assigneeId is { } a && _actors?.GetValueOrDefault(a)?.Kind == ActorKind.Agent);

    private bool AssignedToHuman(TaskItem t) => t.AssigneeId is { } a && _actors?.GetValueOrDefault(a)?.Kind == ActorKind.Human;

    /// <summary>
    /// An agent may only comment on a task another agent holds (assigned to it or live-claimed by it). Everything else,
    /// the user's tasks included, it may edit, move and assign: the user works tasks through agents.
    /// </summary>
    private void EnsureCanWrite(TaskItem t, ActorInfo me)
    {
        if (me.IsHuman) return;
        var now = Now;
        var assignedToOtherAgent = t.AssigneeId is { } a && a != me.Id && _actors?.GetValueOrDefault(a)?.Kind == ActorKind.Agent;
        var claimedByOther = t.ClaimedById is { } c && c != me.Id && ClaimLive(t, now);
        if (assignedToOtherAgent || claimedByOther)
            throw new ForbiddenException("Another agent holds this task. You can only comment on it.");
    }

    /// <summary>As <see cref="EnsureCanWrite"/>, but an agent that was @mentioned on the task (and has not answered yet) may answer in a field.</summary>
    private async Task EnsureCanWriteFieldAsync(TaskItem t, ActorInfo me, CancellationToken ct)
    {
        if (me.IsAgent && await db.Mentions.AnyAsync(m => m.TaskId == t.Id && m.MentionedId == me.Id && m.AnsweredAt == null, ct)) return;
        try { EnsureCanWrite(t, me); }
        catch (ForbiddenException)
        {
            throw new ForbiddenException("Another agent holds this task. You can only comment on it, or write a field while it has @mentioned you for input.");
        }
    }

    /// <summary>The active dispatcher, if there is one: it gets the "any agent" tasks and passes each on.</summary>
    private static Actor? DispatcherOf(Dictionary<int, Actor> actors) =>
        actors.Values.Where(a => a.Kind == ActorKind.Agent && a.IsDispatcher && a.IsActive).OrderBy(a => a.Id).FirstOrDefault();

    private async Task<Dictionary<int, Actor>> ActorsAsync(CancellationToken ct = default) =>
        _actors ??= await db.Actors.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);

    private static string Name(Dictionary<int, Actor> actors, int id) => actors.TryGetValue(id, out var a) ? a.Name : "?";

    private async Task<(int? Id, bool Pool)> ResolveAssigneeAsync(string? s, bool allowPool, CancellationToken ct)
    {
        var actors = await ActorsAsync(ct);   // ensures cache is populated for EnsureCanWrite too
        if (string.IsNullOrWhiteSpace(s) || s.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("unassigned", StringComparison.OrdinalIgnoreCase))
            return (null, false);
        if (s.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
            return allowPool ? (null, true) : throw new ValidationException("'any' is not valid here.");
        var hit = actors.Values.FirstOrDefault(a => a.IsActive && a.Name.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationException($"Unknown assignee '{s}'.");
        return (hit.Id, false);
    }

    private static string? DescribeAssignee(Dictionary<int, Actor> actors, TaskItem t) =>
        t.AssignedToPool ? "any" : t.AssigneeId is { } id ? Name(actors, id) : null;

    private static string RequireLabel(string? label)
    {
        var l = label?.Trim();
        return string.IsNullOrEmpty(l) ? throw new ValidationException("Field label is required.") : l;
    }

    private async Task<double> BottomOrderAsync(int boardId, BoardStatus status, CancellationToken ct)
    {
        var min = await db.Tasks.IgnoreQueryFilters().Where(t => t.BoardId == boardId && t.Status == status).Select(t => (double?)t.SortOrder).MinAsync(ct);
        return min is null ? 0 : min.Value - 1;
    }

    private async Task<double> TopOrderAsync(int boardId, BoardStatus status, CancellationToken ct)
    {
        var max = await db.Tasks.IgnoreQueryFilters().Where(t => t.BoardId == boardId && t.Status == status).Select(t => (double?)t.SortOrder).MaxAsync(ct);
        return max is null ? 0 : max.Value + 1;
    }

    private async Task<double> ComputeSortOrderAsync(int boardId, BoardStatus status, int selfId, int? before, int? after, CancellationToken ct)
    {
        if (before is null && after is null) return await BottomOrderAsyncExcluding(boardId, status, selfId, ct);
        var lane = await db.Tasks.IgnoreQueryFilters().AsNoTracking().Where(t => t.BoardId == boardId && t.Status == status && t.Id != selfId)
            .Select(t => new { t.Id, t.SortOrder }).ToListAsync(ct);
        // A neighbour on another board is a different mistake from one in another lane: say so.
        foreach (var (neighbour, label) in new[] { (before, "beforeTaskId"), (after, "afterTaskId") })
        {
            if (neighbour is { } nid && lane.All(x => x.Id != nid) &&
                await db.Tasks.IgnoreQueryFilters().AnyAsync(t => t.Id == nid && t.BoardId != boardId, ct))
                throw new ValidationException($"{label} belongs to another board. A task is positioned only against tasks on its own board; to put it on another board, move it to that board first.");
        }
        var b = before is { } bi ? lane.FirstOrDefault(x => x.Id == bi) ?? throw new ValidationException("beforeTaskId is not in the target lane.") : null;
        var a = after is { } ai ? lane.FirstOrDefault(x => x.Id == ai) ?? throw new ValidationException("afterTaskId is not in the target lane.") : null;
        if (b is not null && a is not null) return (b.SortOrder + a.SortOrder) / 2;
        if (b is not null)   // directly above b: between b and the next-higher neighbour
        {
            var upper = lane.Where(x => x.SortOrder > b.SortOrder).Select(x => (double?)x.SortOrder).Min();
            return upper is null ? b.SortOrder + 1 : (upper.Value + b.SortOrder) / 2;
        }
        var anchor = a!;
        var lower = lane.Where(x => x.SortOrder < anchor.SortOrder).Select(x => (double?)x.SortOrder).Max();
        return lower is null ? anchor.SortOrder - 1 : (lower.Value + anchor.SortOrder) / 2;
    }

    private async Task<double> BottomOrderAsyncExcluding(int boardId, BoardStatus status, int selfId, CancellationToken ct)
    {
        var min = await db.Tasks.IgnoreQueryFilters().Where(t => t.BoardId == boardId && t.Status == status && t.Id != selfId).Select(t => (double?)t.SortOrder).MinAsync(ct);
        return min is null ? 0 : min.Value - 1;
    }

    private ActivityEvent Record(TaskItem t, EventType type, object? payload)
    {
        var me = Me;
        var now = Now;
        t.LastActivityAt = now;
        t.LastActivityById = me.Id;
        var ev = new ActivityEvent
        {
            BoardId = t.BoardId, TaskId = t.Id, ActorId = me.Id, Type = type, At = now,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
        };
        db.Events.Add(ev);
        return ev;
    }

    private async Task<int?> FindKeyAsync(string scope, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var full = $"{scope}:{key.Trim()}";
        var me = Me.Id;
        return await db.RequestKeys.AsNoTracking().Where(r => r.ActorId == me && r.Key == full).Select(r => (int?)r.Result).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A save failed on the unique (ActorId, Key) because a concurrent call with the same requestKey committed first.
    /// Discards this call's whole change (rolls back, forgets the tracked entities, drops queued broadcasts) and returns
    /// the winner's stored result id, or null when the key is not the cause (the caller rethrows).
    /// </summary>
    private async Task<int?> RecoverKeyRace(string scope, string? key, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        _after.Clear();
        _outboxDirty = false;
        return await FindKeyAsync(scope, key, ct);
    }

    /// <summary>
    /// The idempotency records that point at these tasks or at their comments and subtasks. They go with the rows: a
    /// retried call with the same requestKey must create anew, not "find" a deleted row.
    /// </summary>
    internal static IQueryable<RequestKey> RequestKeysFor(DoiQDbContext db, IQueryable<int> taskIds) =>
        db.RequestKeys.Where(r => (r.Key.StartsWith("task:") && taskIds.Contains(r.Result))
            || (r.Key.StartsWith("comment:") && db.Comments.Any(c => c.Id == r.Result && taskIds.Contains(c.TaskId)))
            || (r.Key.StartsWith("subtask:") && db.Subtasks.Any(s => s.Id == r.Result && taskIds.Contains(s.TaskId))));

    private void RememberKey(string scope, string? key, int result)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        db.RequestKeys.Add(new RequestKey { ActorId = Me.Id, Key = $"{scope}:{key.Trim()}", Result = result, CreatedAt = Now });
    }

    private void After(Func<Task> action) => _after.Add(action);

    private async Task RunAfterAsync()
    {
        var actions = _after.ToList();
        _after.Clear();
        foreach (var a in actions)
        {
            try { await a(); }
            catch (Exception ex) { logger.LogWarning(ex, "Post-commit broadcast failed"); }
        }
        if (_outboxDirty)
        {
            _outboxDirty = false;
            try { outboxSignal.Signal(); } catch (Exception ex) { logger.LogWarning(ex, "Outbox signal failed"); }
        }
    }

    private async Task CommitAsync(CancellationToken ct, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null, Func<Task>? beforeCommit = null)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            if (beforeCommit is not null) await beforeCommit();
            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            _after.Clear();
            throw new ConflictException("The task was changed by someone else. Reload and retry.");
        }
    }

    private async Task PublishChangedAsync(ActivityEvent ev, TaskItem t)
    {
        var me = Me;
        var summary = (await ProjectAsync([t], null, CancellationToken.None))[0];
        await broadcaster.TaskChangedAsync(new TaskChangedMessage(ev.Id, ev.Type, me.Id, me.Kind, summary));
    }

    // ───────────────────────── outbox planning ─────────────────────────

    private static readonly HashSet<EventType> HumanTriggers =
    [
        EventType.Created, EventType.Assigned, EventType.Updated, EventType.Moved, EventType.FieldAdded, EventType.FieldUpdated,
        EventType.CommentAdded, EventType.SubtaskAdded, EventType.SubtaskUpdated, EventType.MovedToBoard,
    ];

    /// <summary>Queues (coalesced, debounced) webhook rows in the same SaveChanges as the change.</summary>
    private async Task PlanWakeupsAsync(ActivityEvent ev, TaskItem t, ActorInfo me, IEnumerable<int> subtaskAssignees, CancellationToken ct)
    {
        var actors = await ActorsAsync(ct);
        if (me.IsHuman)
        {
            if (!HumanTriggers.Contains(ev.Type)) return;
            var targets = new HashSet<int>();
            if (t.AssignedToPool)
            {
                // With a dispatcher, "any agent" means "let the dispatcher pick"; without one the agents race on claim_task.
                if (DispatcherOf(actors) is { } d) targets.Add(d.Id);
                else foreach (var a in actors.Values.Where(a => a.Kind == ActorKind.Agent && a.IsActive)) targets.Add(a.Id);
            }
            else if (t.AssigneeId is { } aid && actors.GetValueOrDefault(aid) is { Kind: ActorKind.Agent, IsActive: true }) targets.Add(aid);
            else if (t.Status == BoardStatus.New) foreach (var a in actors.Values.Where(a => a.IsTriage && a.IsActive)) targets.Add(a.Id);
            foreach (var s in subtaskAssignees) if (actors.GetValueOrDefault(s) is { Kind: ActorKind.Agent, IsActive: true }) targets.Add(s);
            foreach (var target in targets) await UpsertOutboxAsync(OutboxKind.Todo, t, target, ev, null, ct);
        }
        else if (ev.Type is EventType.Created or EventType.Assigned)
        {
            // An agent handing work on (the dispatcher or triage agent assigning it) wakes whoever gets it.
            if (t.AssigneeId is { } who && actors.GetValueOrDefault(who) is { } to)
            {
                if (to.Kind == ActorKind.Human) await UpsertOutboxAsync(OutboxKind.Notify, t, me.Id, ev, $"{me.Name} assigned you: {t.Title}", ct);
                else if (to.Id != me.Id && to.IsActive) await UpsertOutboxAsync(OutboxKind.Todo, t, to.Id, ev, null, ct);
            }
            else if (t.AssignedToPool && DispatcherOf(actors) is { } d && d.Id != me.Id)
                await UpsertOutboxAsync(OutboxKind.Todo, t, d.Id, ev, null, ct);
        }
    }

    /// <summary>
    /// An @mentioned agent is woken whoever wrote the comment (the one exception to "an agent's own changes wake nobody";
    /// the hop limit in AddComment keeps agents from waking each other forever). A mentioned user hears through the
    /// mentioning agent's notify route; the user mentioning themself does nothing.
    /// </summary>
    private async Task PlanMentionWakeupsAsync(ActivityEvent ev, TaskItem t, ActorInfo me, List<Actor> mentioned, CancellationToken ct)
    {
        foreach (var a in mentioned)
        {
            if (a.Kind == ActorKind.Agent) await UpsertOutboxAsync(OutboxKind.Todo, t, a.Id, ev, MentionedReason, ct);
            else if (me.IsAgent) await UpsertOutboxAsync(OutboxKind.Notify, t, me.Id, ev, $"{me.Name} mentioned you: {t.Title}", ct);
        }
    }

    /// <summary>Todo webhooks carry it as "reason" when the agent was @mentioned (asked for input) rather than given the task.</summary>
    public const string MentionedReason = "Mentioned";

    /// <summary>An agent gave the user a subtask: tell the user through the agent's notify route, as for a task.</summary>
    private async Task NotifySubtaskForUserAsync(ActivityEvent ev, TaskItem t, ActorInfo me, int? assigneeId, string subtaskTitle, CancellationToken ct)
    {
        if (!me.IsAgent || assigneeId is not { } who || (await ActorsAsync(ct)).GetValueOrDefault(who)?.Kind != ActorKind.Human) return;
        await UpsertOutboxAsync(OutboxKind.Notify, t, me.Id, ev, $"{me.Name} assigned you a subtask: {subtaskTitle} (on {t.Title})", ct);
    }

    private async Task UpsertOutboxAsync(OutboxKind kind, TaskItem t, int target, ActivityEvent ev, string? payload, CancellationToken ct)
    {
        // A row this same change already queued (e.g. the assignee who is also @mentioned) is not in the database yet.
        var existing = db.Outbox.Local.FirstOrDefault(o => o.Id == 0 && o.Kind == kind && o.TaskId == t.Id && o.TargetActorId == target)
            ?? (t.Id == 0 ? null : await db.Outbox.FirstOrDefaultAsync(o => o.Kind == kind && o.TaskId == t.Id && o.TargetActorId == target && o.SentAt == null, ct));
        if (existing is not null)
        {
            existing.Event = ev;   // keep the original NotBefore: one webhook per window
            existing.PayloadJson = payload ?? existing.PayloadJson;
        }
        else
        {
            var now = Now;
            db.Outbox.Add(new OutboxMessage
            {
                Kind = kind, BoardId = t.BoardId, TaskId = t.Id, TargetActorId = target, Event = ev, PayloadJson = payload,
                CreatedAt = now, NotBefore = now.AddSeconds(_board.DebounceSeconds),
            });
        }
        _outboxDirty = true;
    }

    // ───────────────────────── projection ─────────────────────────

    private async Task<Dictionary<int, long>> LatestEventsAsync(List<int> ids, CancellationToken ct) =>
        (await db.Events.AsNoTracking().Where(e => ids.Contains(e.TaskId)).GroupBy(e => e.TaskId)
            .Select(g => new { g.Key, Max = g.Max(e => e.Id) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Max);

    private async Task<List<TaskSummaryDto>> ProjectAsync(IReadOnlyCollection<TaskItem> tasks, ActorInfo? viewer, CancellationToken ct)
    {
        if (tasks.Count == 0) return [];
        var actors = await ActorsAsync(ct);
        var boards = await BoardsAsync(ct);
        var ids = tasks.Select(t => t.Id).ToList();
        var comments = await db.Comments.AsNoTracking().Where(c => ids.Contains(c.TaskId)).GroupBy(c => c.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var fields = await db.Fields.AsNoTracking().Where(f => ids.Contains(f.TaskId)).GroupBy(f => f.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var subs = await db.Subtasks.AsNoTracking().Where(s => ids.Contains(s.TaskId)).GroupBy(s => s.TaskId)
            .Select(g => new { g.Key, Total = g.Count(), Done = g.Count(s => s.Done) }).ToDictionaryAsync(x => x.Key, ct);

        Dictionary<int, long> otherLatest = [];
        Dictionary<int, long> seen = [];
        if (viewer is not null)
        {
            var vid = viewer.Id;
            otherLatest = (await db.Events.AsNoTracking().Where(e => e.ActorId != vid && ids.Contains(e.TaskId)).GroupBy(e => e.TaskId)
                .Select(g => new { g.Key, Max = g.Max(e => e.Id) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Max);
            seen = await db.ReadMarkers.AsNoTracking().Where(m => m.ActorId == vid && ids.Contains(m.TaskId)).ToDictionaryAsync(m => m.TaskId, m => m.SeenThroughEventId, ct);
        }

        var now = Now;
        var cutoff = db.ArchiveCutoff;
        return tasks.Select(t =>
        {
            var unread = viewer is not null && otherLatest.TryGetValue(t.Id, out var o) && o > seen.GetValueOrDefault(t.Id);
            var needsMe = viewer is { IsHuman: true } && ((t.AssigneeId == viewer.Id && t.Status == BoardStatus.Waiting) || unread);
            var live = ClaimLive(t, now);
            subs.TryGetValue(t.Id, out var sub);
            return new TaskSummaryDto(
                t.Id, t.Title, t.Status, DescribeAssignee(actors, t), t.AssigneeId, t.AssignedToPool, t.DueDate, t.SortOrder,
                t.CreatedAt, t.UpdatedAt, t.DoneAt, t.LastActivityAt, Name(actors, t.LastActivityById), t.Version, t.Source, t.SourceRef,
                live ? Name(actors, t.ClaimedById!.Value) : null, live ? t.ClaimExpiresAt : null,
                comments.GetValueOrDefault(t.Id), fields.GetValueOrDefault(t.Id), sub?.Total ?? 0, sub?.Done ?? 0, unread, needsMe, t.BoardId, BoardNameOf(boards, t.BoardId),
                Archived: t.Status == BoardStatus.Done && t.DoneAt is { } done && done < cutoff);
        }).ToList();
    }

    private static InfoFieldDto ToDto(InfoField f, Dictionary<int, Actor> actors) =>
        new(f.Id, f.TaskId, f.Label, f.Value, f.AuthorId, Name(actors, f.AuthorId), f.CreatedAt, f.UpdatedAt);

    private static CommentDto ToDto(Comment c, Dictionary<int, Actor> actors, long eventId = 0, ActorInfo? viewer = null, long viewerSeen = 0, IEnumerable<MentionDto>? mentions = null) =>
        new(c.Id, c.TaskId, c.AuthorId, Name(actors, c.AuthorId), c.Body, c.CreatedAt, c.EditedAt, eventId,
            Unread: viewer is not null && c.AuthorId != viewer.Id && eventId > viewerSeen,
            Mentions: mentions?.ToList() is { Count: > 0 } list ? list : null);

    private static SubtaskDto ToDto(Subtask s, Dictionary<int, Actor> actors) =>
        new(s.Id, s.TaskId, s.Title, s.Done, s.AssigneeId is { } a ? Name(actors, a) : null, s.AssigneeId, s.SortOrder, Name(actors, s.CreatedById), s.DoneAt);
}

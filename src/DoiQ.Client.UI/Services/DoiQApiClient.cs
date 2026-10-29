using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DoiQ.Client.Models;
using DoiQ.Shared.Enums;

namespace DoiQ.Client.Services;

/// <summary>
/// HttpClient implementation against the DoiQ server. The board lives in a <see cref="BoardState"/> that REST answers
/// and pushed hub events both update; <see cref="Changed"/> fires when a pushed event changed it.
/// </summary>
public sealed class DoiQApiClient(HttpClient http) : IDoiQApiClient, IBoardSync
{
    private readonly BoardState _state = new();
    private readonly SemaphoreSlim _gate = new(1, 1);   // load, apply and catch-up never interleave
    private readonly HashSet<int> _detailed = [];       // tasks whose description and lists are loaded (the board load brings summaries only)
    private readonly Dictionary<int, TaskVm> _offBoard = [];   // archived tasks opened from the archive view or a search: never on the board
    private readonly Dictionary<int, long> _shownThrough = [];   // per task, the newest event this client has taken in (what marking seen may cover)
    private ActorDto? _me;
    private int _archiveAfterDays = 14;
    private DateTime _loadedAtUtc;
    private IReadOnlyList<string> _agentNames = [];
    private string? _dispatcher;
    private PresenceVm _presence = new(0, null, null);
    private DateTime _presenceAt = DateTime.MinValue;

    private int _boardId;
    private IReadOnlyList<BoardDto> _boards = [];

    public event Action? Changed;
    public event Action? Unauthorized;
    public event Action? BoardsChanged;

    // ---- boards ----

    public IReadOnlyList<BoardDto> Boards => _boards;
    public int CurrentBoardId => _boardId;

    public async Task<IReadOnlyList<BoardDto>> GetBoardsAsync()
    {
        _boards = await Send<List<BoardDto>>(HttpMethod.Get, "api/boards") ?? [];
        return _boards;
    }

    public async Task SelectBoardAsync(int boardId)
    {
        await _gate.WaitAsync();
        try
        {
            if (boardId == _boardId) return;
            _boardId = boardId;
            _state.Clear();   // the next load fetches the new board; nothing of the old one stays on screen
            _detailed.Clear();
            _offBoard.Clear();
            _shownThrough.Clear();
            ArchivedCount = 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<BoardDto> CreateBoardAsync(string name)
    {
        var b = await Send<BoardDto>(HttpMethod.Post, "api/boards", new CreateBoardRequest(name)) ?? throw new ApiException("Empty response from the server.");
        await GetBoardsAsync();
        BoardsChanged?.Invoke();
        return b;
    }

    public async Task<BoardDto> RenameBoardAsync(int id, string name, long? expectedVersion)
    {
        var b = await Send<BoardDto>(HttpMethod.Put, $"api/boards/{id}", new RenameBoardRequest(name, expectedVersion)) ?? throw new ApiException("Empty response from the server.");
        await GetBoardsAsync();
        BoardsChanged?.Invoke();
        return b;
    }

    public async Task DeleteBoardAsync(int id)
    {
        await Send<object>(HttpMethod.Delete, $"api/boards/{id}?confirm=true");
        await GetBoardsAsync();
        BoardsChanged?.Invoke();
    }

    public async Task ApplyAsync(BoardChangedMessage m)
    {
        await GetBoardsAsync();
        BoardsChanged?.Invoke();
    }

    // ---- board ----

    public IReadOnlyList<string> Agents => _agentNames;

    public string? Dispatcher => _dispatcher;

    public int ArchivedCount { get; private set; }

    public async Task<IReadOnlyList<TaskVm>> GetBoard(CancellationToken ct)
    {
        await EnsureLoaded(ct);
        await _gate.WaitAsync(ct);
        try
        {
            PruneArchived();
            return _state.Tasks.ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureLoaded(CancellationToken ct)
    {
        if (_state.IsLoaded) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_state.IsLoaded) await Load(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The board in four requests whatever its size: boards, me, actors and the board load (active tasks with their
    /// counts, the archived count and the catch-up baseline, which the server reads before the tasks). The drawer loads
    /// a task's description and lists when it opens.
    /// </summary>
    private async Task Load(CancellationToken ct)
    {
        var boards = _boards = await Send<List<BoardDto>>(HttpMethod.Get, "api/boards", null, true, ct) ?? [];
        // Nothing selected yet (or the selected board is gone): the first board.
        var boardId = boards.Any(b => b.Id == _boardId) ? _boardId : boards.FirstOrDefault()?.Id ?? 0;
        var me = await Send<ActorDto>(HttpMethod.Get, "api/me", null, true, ct) ?? throw new ApiException("Not signed in.");
        var actors = await Send<List<ActorDto>>(HttpMethod.Get, "api/actors", null, true, ct) ?? [];
        var load = await Send<BoardLoadDto>(HttpMethod.Get, $"api/boards/{boardId}/tasks", null, true, ct) ?? throw new ApiException("Empty response from the server.");

        var now = DateTime.UtcNow;
        _boardId = boardId;
        _me = me;
        _agentNames = actors.Where(a => a.Kind == ActorKind.Agent).Select(a => a.Name).ToList();
        _dispatcher = actors.FirstOrDefault(a => a.Kind == ActorKind.Agent && a.IsDispatcher)?.Name;
        _detailed.Clear();
        _offBoard.Clear();
        _shownThrough.Clear();
        _archiveAfterDays = load.ArchiveAfterDays;
        _loadedAtUtc = now;
        ArchivedCount = load.ArchivedCount;
        _state.Reset(load.Tasks.Where(s => !s.Archived).Select(s => TaskMapper.FromSummary(s, me)), load.LatestEventId);
    }

    /// <summary>Done tasks that aged past the archive window while the board was open leave it (the server archives by age, nothing is pushed).</summary>
    private void PruneArchived()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_archiveAfterDays);
        foreach (var t in _state.Tasks.Where(t => t.Lane == BoardLane.Done && t.DoneAt < cutoff).ToList())
        {
            _state.Remove(t.Id);
            _detailed.Remove(t.Id);
            ArchivedCount++;
        }
    }

    public TaskVm? Find(int id) => _state.Find(id) ?? _offBoard.GetValueOrDefault(id);

    public async Task<TaskVm?> LoadTask(int id, CancellationToken ct)
    {
        await EnsureLoaded(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var d = await FetchDetail(id, ct);
            if (d is null || d.Task.BoardId != _boardId) { Forget(id); return null; }
            StoreDetail(d);
            return Find(id);
        }
        finally { _gate.Release(); }
    }

    public async Task<ArchivePageVm> GetArchivePage(int boardId, string? query, string? cursor, CancellationToken ct)
    {
        await EnsureLoaded(ct);
        var url = $"api/boards/{boardId}/archive?limit=50"
            + (string.IsNullOrWhiteSpace(query) ? "" : $"&q={Uri.EscapeDataString(query.Trim())}")
            + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        var page = await Send<TaskPageDto>(HttpMethod.Get, url, null, true, ct) ?? throw new ApiException("Empty response from the server.");
        return new ArchivePageVm(page.Tasks.Select(s => TaskMapper.ToArchived(s, Me.Id)).ToList(), page.NextCursor);
    }

    public async Task<BoardSearchVm> SearchBoard(string query, CancellationToken ct)
    {
        await EnsureLoaded(ct);
        var q = query.Trim();
        var r = await Send<BoardSearchDto>(HttpMethod.Get, $"api/boards/{_boardId}/search?q={Uri.EscapeDataString(q)}&archivedLimit=10", null, true, ct)
            ?? throw new ApiException("Empty response from the server.");
        return new BoardSearchVm(q, r.ActiveTaskIds.ToHashSet(), r.Archived.Tasks.Select(s => TaskMapper.ToArchived(s, Me.Id)).ToList(), r.Archived.NextCursor is not null);
    }

    public void Reset()
    {
        _lifetime.Cancel();
        _lifetime = new CancellationTokenSource();
        _catchUpScheduled = false;
        _state.Clear();
        _detailed.Clear();
        _offBoard.Clear();
        _shownThrough.Clear();
        ArchivedCount = 0;
        _me = null;
        _boardId = 0;
        _boards = [];
        _presenceAt = DateTime.MinValue;
    }

    public async Task<PresenceVm> GetPresenceAsync()
    {
        if (DateTime.UtcNow - _presenceAt < TimeSpan.FromSeconds(30)) return _presence;
        try
        {
            var agents = (await Send<List<AgentAdminDto>>(HttpMethod.Get, "api/agents") ?? []).Where(a => a.IsActive).ToList();
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            var recent = agents.Where(a => a.LastSeenAt is { } s && s >= cutoff).ToList();
            var last = agents.Where(a => a.LastSeenAt is not null).MaxBy(a => a.LastSeenAt);
            _presence = new PresenceVm(recent.Count, last?.Name, last?.LastSeenAt is { } seen ? TaskMapper.At(seen) : null);
        }
        catch (ApiException) { /* keep the last answer */ }
        _presenceAt = DateTime.UtcNow;
        return _presence;
    }

    // ---- task writes: the answer goes straight into the board, the hub echo then changes nothing ----

    private ActorDto Me => _me ?? throw new ApiException("The board is not loaded yet.");

    public async Task<TaskVm> CreateTaskAsync(string title)
    {
        var d = await Send<TaskDetailDto>(HttpMethod.Post, "api/tasks", new CreateTaskRequest(title.Trim(), BoardId: _boardId == 0 ? null : _boardId)) ?? throw new ApiException("Empty response from the server.");
        StoreDetail(d);
        return Find(d.Task.Id) ?? TaskMapper.FromDetail(d, Me);
    }

    public Task SetTitleAsync(int id, string title, long? baseVersion = null) => PatchAsync(id, new UpdateTaskRequest(Title: title.Trim()), baseVersion);
    public Task SetDescriptionAsync(int id, string description, long? baseVersion = null) => PatchAsync(id, new UpdateTaskRequest(Description: description), baseVersion);
    public Task SetDueAsync(int id, DateOnly? due, long? baseVersion = null) => PatchAsync(id, new UpdateTaskRequest(DueDate: due, ClearDueDate: due is null), baseVersion);

    private async Task PatchAsync(int id, UpdateTaskRequest req, long? baseVersion)
    {
        req = req with { ExpectedVersion = baseVersion ?? VersionOf(id) };
        var s = await SendVersioned<TaskSummaryDto>(id, HttpMethod.Patch, $"api/tasks/{id}", req) ?? throw new ApiException("Empty response from the server.");
        // The description is not in a summary, so the edit is applied locally as well.
        MergeSummary(s, byOther: false);
        if (req.Description is not null && Find(id) is { } t) Replace(t with { Description = req.Description });
    }

    public async Task MoveAsync(int id, BoardLane lane, int? beforeId)
    {
        var req = new MoveTaskRequest(TaskMapper.Status(lane), BeforeTaskId: beforeId == id ? null : beforeId, ExpectedVersion: VersionOf(id));
        var s = await SendVersioned<TaskSummaryDto>(id, HttpMethod.Post, $"api/tasks/{id}/move", req) ?? throw new ApiException("Empty response from the server.");
        MergeSummary(s, byOther: false);
    }

    public async Task AssignAsync(int id, AssigneeVm assignee)
    {
        var who = assignee.Kind switch
        {
            AssigneeKind.User => Me.Name,
            AssigneeKind.Agent => assignee.Agent,
            AssigneeKind.Pool => "any",
            _ => null,
        };
        var s = await SendVersioned<TaskSummaryDto>(id, HttpMethod.Post, $"api/tasks/{id}/assign", new AssignRequest(who, VersionOf(id))) ?? throw new ApiException("Empty response from the server.");
        MergeSummary(s, byOther: false);
    }

    /// <summary>The version of the task this client last saw (null when it holds none: the server then does not check).</summary>
    public async Task MoveToBoard(int id, int boardId, CancellationToken ct)
    {
        var req = new MoveToBoardRequest(boardId, ExpectedVersion: VersionOf(id));
        var s = await SendVersioned<TaskSummaryDto>(id, HttpMethod.Post, $"api/tasks/{id}/move-to-board", req, ct) ?? throw new ApiException("Empty response from the server.");
        if (s.BoardId == _boardId) MergeSummary(s, byOther: false);
        else Leave(id);
        await GetBoardsAsync();   // the open and done counts of both boards changed
    }

    private long? VersionOf(int id) => Find(id) is { Version: > 0 } t ? t.Version : null;

    /// <summary>
    /// A write that carries the task's version. A 409 means someone else changed it first: the task is reloaded so the
    /// board shows their version, then a <see cref="TaskConflictException"/> says who it was. A 404 drops the task.
    /// </summary>
    private Task<T?> SendVersioned<T>(int id, HttpMethod method, string url, object body) =>
        SendVersioned<T>(id, method, url, body, CancellationToken.None);

    private async Task<T?> SendVersioned<T>(int id, HttpMethod method, string url, object body, CancellationToken ct)
    {
        try { return await Send<T>(method, url, body, true, ct); }
        catch (ApiException e) when (e.Status == HttpStatusCode.Conflict)
        {
            try { await RefreshAsync(id); } catch (ApiException) { /* the next catch-up repairs it */ }
            var by = Find(id)?.UpdatedBy ?? "someone";
            throw new TaskConflictException(id, by, e.Message);
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            Forget(id);
            throw;
        }
    }

    public async Task DeleteTaskAsync(int id)
    {
        await Send<object>(HttpMethod.Delete, $"api/tasks/{id}");
        Forget(id);
    }

    public async Task MarkSeenAsync(int id)
    {
        if (!_detailed.Contains(id)) await RefreshAsync(id);   // a task the board loaded without its lists
        // Only through what this client has taken in: an event that lands meanwhile stays unread until it is shown.
        var through = _shownThrough.GetValueOrDefault(id);
        await Send<object>(HttpMethod.Post, $"api/tasks/{id}/seen", new SeenRequest(through > 0 ? through : null));
        if (Find(id) is { } t)
            Replace(t with
            {
                Unread = false,
                Comments = t.Comments.Select(c => c with { IsNew = false }).ToList(),
                Fields = t.Fields.Select(f => f with { IsNew = false }).ToList(),
            });
    }

    public async Task AddSubtaskAsync(int id, string title)
    {
        await Send<SubtaskDto>(HttpMethod.Post, $"api/tasks/{id}/subtasks", new AddSubtaskRequest(title.Trim()));
        await RefreshAsync(id);
    }

    public async Task SetSubtaskDoneAsync(int id, int subtaskId, bool done)
    {
        await Send<SubtaskDto>(HttpMethod.Patch, $"api/tasks/{id}/subtasks/{subtaskId}", new UpdateSubtaskRequest(Done: done));
        await RefreshAsync(id);
    }

    public async Task AddFieldAsync(int id, string name, string body)
    {
        await Send<InfoFieldDto>(HttpMethod.Post, $"api/tasks/{id}/fields", new FieldRequest(name.Trim(), body));
        await RefreshAsync(id);
    }

    public async Task UpdateFieldAsync(int id, int fieldId, string name, string body)
    {
        await Send<InfoFieldDto>(HttpMethod.Put, $"api/tasks/{id}/fields/{fieldId}", new FieldRequest(name.Trim(), body));
        await RefreshAsync(id);
    }

    public async Task AddCommentAsync(int id, string body)
    {
        await Send<CommentDto>(HttpMethod.Post, $"api/tasks/{id}/comments", new AddCommentRequest(body.Trim()));
        await RefreshAsync(id);
    }

    // ---- pushed events ----

    public async Task ApplyAsync(TaskChangedMessage m)
    {
        await ApplyEventAsync(m.EventId, m.Task.BoardId, m.Task.Id, async () =>
        {
            var byOther = m.ActorId != Me.Id;
            var existing = _state.Find(m.Task.Id);
            // A summary carries no description or lists: refetch when the event changed one of them (or the task is new here).
            var subtaskEvent = m.Type is EventType.SubtaskAdded or EventType.SubtaskUpdated or EventType.SubtaskDeleted;
            var arrived = m.Type == EventType.MovedToBoard;
            var needsDetail = existing is null || arrived || (m.Type == EventType.Updated && byOther) || (subtaskEvent && _detailed.Contains(existing.Id));
            return needsDetail ? await RefreshAsync(m.Task.Id, arrived) : MergeSummary(m.Task, byOther);
        });
    }

    public Task ApplyAsync(TaskDeletedMessage m) => ApplyEventAsync(m.EventId, m.BoardId, m.TaskId, () => Task.FromResult(Forget(m.TaskId)));

    public async Task ApplyAsync(CommentAddedMessage m)
    {
        await ApplyEventAsync(m.EventId, m.BoardId, m.TaskId, async () =>
        {
            var byOther = m.ActorId != Me.Id;
            // Unknown here, or known by its counts only: the detail brings the new count (and an archived task stays off the board).
            if (_state.Find(m.TaskId) is null || !_detailed.Contains(m.TaskId)) return await RefreshAsync(m.TaskId);
            var c = m.Comment;
            return _state.AddComment(m.TaskId, new CommentVm(c.Id, c.Author, c.Body, TaskMapper.At(c.CreatedAt), IsNew: byOther, EventId: m.EventId, Mentions: c.Mentions), unread: byOther);
        });
    }

    /// <summary>A read receipt. Not an activity event (no id to dedupe or catch up on), so it is applied directly, in order, under the gate.</summary>
    public async Task ApplyAsync(SeenChangedMessage m)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_state.IsLoaded || m.ActorId == Me.Id || m.BoardId != _boardId) return;
            if (_state.ApplySeen(m.TaskId, m.Actor, m.SeenThroughEventId)) Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyAsync(FieldChangedMessage m)
    {
        await ApplyEventAsync(m.EventId, m.BoardId, m.TaskId, async () =>
        {
            var byOther = m.ActorId != Me.Id;
            if (_state.Find(m.TaskId) is null || !_detailed.Contains(m.TaskId)) return await RefreshAsync(m.TaskId);
            var f = m.Field;
            var vm = f is null ? null : new InfoFieldVm(f.Id, f.Label, f.Value, f.Author, TaskMapper.At(f.UpdatedAt), IsNew: byOther);
            return _state.ApplyField(m.TaskId, m.FieldId, vm, unread: byOther);
        });
    }

    /// <summary>How long a gap in the event ids may stay open before a catch-up fills it (the missing event usually arrives within milliseconds).</summary>
    public TimeSpan CatchUpDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    private bool _catchUpScheduled;
    private CancellationTokenSource _lifetime = new();   // cancelled by Reset: a signed-out client runs no catch-up

    /// <summary>
    /// Applies one pushed event. Event ids are global, so an event of another board is only taken in (it keeps the ids
    /// consecutive) and never applied, except that a task this board holds has left it for that board. The cursor moves
    /// only through consecutive ids: an event that arrives ahead of an earlier one is applied, but leaves a gap that a
    /// debounced catch-up fills.
    /// </summary>
    private async Task ApplyEventAsync(long eventId, int boardId, int taskId, Func<Task<bool>> apply)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_state.IsLoaded || _state.IsApplied(eventId)) return;
            var changed = boardId == _boardId ? await apply() : Holds(taskId) && Leave(taskId);
            if (boardId == _boardId) TookIn(taskId, eventId);
            _state.TryAdvance(eventId);
            if (_state.HasGap) ScheduleCatchUp();
            if (changed) Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    private void ScheduleCatchUp()
    {
        if (_catchUpScheduled) return;
        _catchUpScheduled = true;
        _ = RunScheduledCatchUp(_lifetime.Token);
    }

    private async Task RunScheduledCatchUp(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CatchUpDelay, ct);
            _catchUpScheduled = false;
            if (_state.HasGap) await CatchUpAsync();
        }
        catch (Exception)
        {
            _catchUpScheduled = false;   // offline or signed out: the next pushed event or the reconnect catches up
        }
    }

    public async Task CatchUpAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_state.IsLoaded) return;
            var cursor = _state.LastEventId;
            var touched = new HashSet<int>();
            var deleted = new HashSet<int>();
            var left = new HashSet<int>();      // held here, but moved to another board
            var arrived = new HashSet<int>();   // moved here from another board
            for (var more = true; more;)
            {
                // Every board's events: the ids of the other boards are part of the sequence the cursor walks through.
                var feed = await Send<EventFeedDto>(HttpMethod.Get, $"api/events?after={cursor}&limit=100") ?? throw new ApiException("Empty response from the server.");
                foreach (var e in feed.Events)
                {
                    if (_state.IsApplied(e.Id)) continue;
                    if (e.BoardId != _boardId)
                    {
                        if (e.Type == EventType.MovedToBoard && Holds(e.TaskId)) left.Add(e.TaskId);
                        continue;
                    }
                    if (e.Type == EventType.Deleted) { deleted.Add(e.TaskId); touched.Remove(e.TaskId); }
                    else if (!deleted.Contains(e.TaskId)) touched.Add(e.TaskId);
                    if (e.Type == EventType.MovedToBoard) arrived.Add(e.TaskId);
                }
                cursor = feed.NextCursor;
                more = feed.HasMore;
            }

            var changed = false;
            foreach (var id in left) changed |= Leave(id);
            foreach (var id in deleted) changed |= Forget(id);
            foreach (var id in touched) changed |= await RefreshAsync(id, arrived.Contains(id));
            _state.CatchUpTo(cursor);
            if (changed) Changed?.Invoke();

            // A reconnect also covers board changes missed while away.
            var before = _boards;
            if (!(await GetBoardsAsync()).SequenceEqual(before)) BoardsChanged?.Invoke();
        }
        finally { _gate.Release(); }
    }

    // ---- helpers ----

    /// <summary>
    /// Refetches one task's detail into the board (or drops it when it is gone). <paramref name="arrived"/>: it was just
    /// moved here from another board. True when the board changed.
    /// </summary>
    private async Task<bool> RefreshAsync(int id, bool arrived = false)
    {
        var d = await FetchDetailAsync(id);
        if (d is null) return Forget(id);
        if (d.Task.BoardId != _boardId) return Leave(id);   // on another board (moved away, or never shown here)
        return StoreDetail(d, arrived);
    }

    private async Task<TaskDetailDto?> FetchDetailAsync(int id)
    {
        try { return await Send<TaskDetailDto>(HttpMethod.Get, $"api/tasks/{id}"); }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    private async Task<TaskDetailDto?> FetchDetail(int id, CancellationToken ct)
    {
        try { return await Send<TaskDetailDto>(HttpMethod.Get, $"api/tasks/{id}", null, true, ct); }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>Stores a full task: on the board, or (archived) aside, where it never shows on the board. True when the board changed.</summary>
    private bool StoreDetail(TaskDetailDto d, bool arrived = false)
    {
        TookIn(d.Task.Id, d.LatestEventId);
        return Store(TaskMapper.FromDetail(d, Me), detailed: true, arrived);
    }

    private void TookIn(int taskId, long eventId)
    {
        if (eventId > _shownThrough.GetValueOrDefault(taskId)) _shownThrough[taskId] = eventId;
    }

    private bool MergeSummary(TaskSummaryDto s, bool byOther)
    {
        if (s.BoardId != _boardId) return false;
        var vm = Find(s.Id) is { } existing ? TaskMapper.Merge(existing, s, Me, byOther) : TaskMapper.FromSummary(s, Me);
        return Store(vm, detailed: _detailed.Contains(s.Id));
    }

    /// <summary>
    /// The one place a task enters or leaves the board. An archived task is kept aside (a hub event for it must not put
    /// it back on the board); a task that comes out of the archive (it existed when the board loaded, but the board did
    /// not have it) lowers the archived count. A task that <paramref name="arrived"/> from another board was in neither:
    /// it raises the count when it is archived and leaves it alone when it is not.
    /// </summary>
    private bool Store(TaskVm vm, bool detailed, bool arrived = false)
    {
        if (detailed) _detailed.Add(vm.Id); else _detailed.Remove(vm.Id);
        if (vm.Archived)
        {
            var known = _offBoard.ContainsKey(vm.Id);
            _offBoard[vm.Id] = _offBoard.TryGetValue(vm.Id, out var held) && vm.Version < held.Version ? held : vm;
            if (!_state.Remove(vm.Id) && (!arrived || known)) return false;
            ArchivedCount++;
            return true;
        }
        var wasAside = _offBoard.Remove(vm.Id);
        var isNew = _state.Find(vm.Id) is null;
        var changed = _state.Upsert(vm);
        if (isNew && changed && !arrived && (wasAside || vm.CreatedAt.UtcDateTime < _loadedAtUtc) && ArchivedCount > 0) ArchivedCount--;
        return changed;
    }

    /// <summary>Replaces a task's view model wherever it is held.</summary>
    private void Replace(TaskVm vm)
    {
        if (!_state.Replace(vm) && _offBoard.ContainsKey(vm.Id)) _offBoard[vm.Id] = vm;
    }

    /// <summary>True when this client holds the task: on the board, or archived and set aside.</summary>
    private bool Holds(int id) => _state.Find(id) is not null || _offBoard.ContainsKey(id);

    /// <summary>Drops a task that is now on another board; one that was archived here no longer counts. True when the board changed.</summary>
    private bool Leave(int id)
    {
        var archived = _offBoard.ContainsKey(id);
        var changed = Forget(id);
        if (archived && ArchivedCount > 0) { ArchivedCount--; return true; }
        return changed;
    }

    /// <summary>Drops a task (deleted, gone or on another board). True when the board changed.</summary>
    private bool Forget(int id)
    {
        _detailed.Remove(id);
        _offBoard.Remove(id);
        _shownThrough.Remove(id);
        return _state.Remove(id);
    }

    // ---- session, setup and administration ----

    public async Task<SetupStatusDto> GetSetupStatusAsync() =>
        await Send<SetupStatusDto>(HttpMethod.Get, "api/setup/status") ?? new SetupStatusDto(false);

    public Task SetupAsync(string password) => Send<object>(HttpMethod.Post, "api/setup", new SetupRequest(password));

    public async Task<bool> IsSignedInAsync()
    {
        using var resp = await http.GetAsync("api/me");
        return resp.IsSuccessStatusCode;
    }

    public Task LoginAsync(string password) => Send<object>(HttpMethod.Post, "api/auth/login", new LoginRequest(password), raiseUnauthorized: false);

    public async Task LogoutAsync()
    {
        await Send<object>(HttpMethod.Post, "api/auth/logout", raiseUnauthorized: false);
        Reset();
    }

    public Task ChangePasswordAsync(string current, string newPassword) =>
        Send<object>(HttpMethod.Post, "api/account/password", new ChangePasswordRequest(current, newPassword));

    public async Task<IReadOnlyList<AgentAdminDto>> GetAgentsAsync() =>
        await Send<List<AgentAdminDto>>(HttpMethod.Get, "api/agents") ?? [];

    public async Task<CreatedAgentResult> CreateAgentAsync(CreateAgentRequest request) =>
        await Send<CreatedAgentResult>(HttpMethod.Post, "api/agents", request) ?? throw new ApiException("Empty response from the server.");

    public async Task<AgentAdminDto> UpdateAgentAsync(int id, UpdateAgentRequest request) =>
        await Send<AgentAdminDto>(HttpMethod.Put, $"api/agents/{id}", request) ?? throw new ApiException("Empty response from the server.");

    public async Task<RotatedKeyResult> RotateAgentKeyAsync(int id) =>
        await Send<RotatedKeyResult>(HttpMethod.Post, $"api/agents/{id}/rotate-key") ?? throw new ApiException("Empty response from the server.");

    public Task SetAgentActiveAsync(int id, bool active) =>
        Send<object>(HttpMethod.Post, $"api/agents/{id}/{(active ? "activate" : "deactivate")}");

    public async Task<DeletedAgentResult> DeleteAgent(int id, CancellationToken ct) =>
        await Send<DeletedAgentResult>(HttpMethod.Delete, $"api/agents/{id}", null, true, ct) ?? throw new ApiException("Empty response from the server.");

    public Task AskForResponsibilities(int agentId, CancellationToken ct) =>
        Send<object>(HttpMethod.Post, $"api/agents/{agentId}/ask-responsibilities", null, true, ct);

    // The calls that predate cancellation tokens; the board load, the archive and the search pass theirs.
    private Task<T?> Send<T>(HttpMethod method, string url, object? body = null, bool raiseUnauthorized = true) =>
        Send<T>(method, url, body, raiseUnauthorized, CancellationToken.None);

    private async Task<T?> Send<T>(HttpMethod method, string url, object? body, bool raiseUnauthorized, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new ApiException("Could not reach the server."); }

        using (resp)
        {
            if (resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == HttpStatusCode.NoContent || resp.Content.Headers.ContentLength == 0) return default;
                try { return await resp.Content.ReadFromJsonAsync<T>(ct); }
                catch (JsonException) { return default; }
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized && raiseUnauthorized) Unauthorized?.Invoke();
            throw new ApiException(await ProblemText(resp, ct), resp.StatusCode);
        }
    }

    private static async Task<string> ProblemText(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            foreach (var key in new[] { "detail", "title", "error", "message" })
                if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    return s;
        }
        catch { /* not JSON */ }
        return resp.StatusCode == HttpStatusCode.Unauthorized ? "Not signed in." : $"The server answered {(int)resp.StatusCode}.";
    }
}

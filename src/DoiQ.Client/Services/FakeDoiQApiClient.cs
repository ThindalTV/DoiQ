using DoiQ.Client.Models;

namespace DoiQ.Client.Services;

/// <summary>In-memory stand-in seeded with the design canvas's sample cards.</summary>
public sealed class FakeDoiQApiClient : IDoiQApiClient
{
    private readonly Dictionary<int, List<TaskVm>> _byBoard = new() { [1] = new() };
    private List<TaskVm> _tasks;
    private int _nextTask = 100;
    private int _nextChild = 1000;
    private int _nextBoard = 2;
    private readonly List<BoardDto> _boards = [new(1, "Main", 0, 0, DateTime.UtcNow.AddDays(-30), 1)];

    public event Action? Changed;
    public event Action? BoardsChanged;

    public IReadOnlyList<string> Agents { get; } = new[] { "Hermes", "Hermes 2" };

    public string? Dispatcher => _agents.FirstOrDefault(a => a.IsDispatcher && a.IsActive)?.Name;

    public FakeDoiQApiClient()
    {
        _tasks = _byBoard[1];
        Seed();
    }

    // ---- boards (in memory; the sample cards live on Main) ----

    public IReadOnlyList<BoardDto> Boards => Counted();
    public int CurrentBoardId { get; private set; } = 1;

    private List<BoardDto> Counted() => _boards.Select(b =>
    {
        var list = _byBoard.GetValueOrDefault(b.Id) ?? [];
        return b with { OpenTasks = list.Count(t => t.Lane != BoardLane.Done), DoneTasks = list.Count(t => t.Lane == BoardLane.Done) };
    }).ToList();

    public Task<IReadOnlyList<BoardDto>> GetBoardsAsync() => Task.FromResult<IReadOnlyList<BoardDto>>(Counted());

    public Task SelectBoardAsync(int boardId)
    {
        if (_boards.All(b => b.Id != boardId)) throw new ApiException($"Board {boardId} not found.", System.Net.HttpStatusCode.NotFound);
        CurrentBoardId = boardId;
        _tasks = _byBoard[boardId];
        return Task.CompletedTask;
    }

    private string CheckName(string name, int? except)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) throw new ApiException("A board name is required.", System.Net.HttpStatusCode.BadRequest);
        if (n.Length > 60) throw new ApiException("A board name can be at most 60 characters.", System.Net.HttpStatusCode.BadRequest);
        if (_boards.Any(b => b.Id != except && b.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException($"A board named '{n}' already exists.", System.Net.HttpStatusCode.Conflict);
        return n;
    }

    public Task<BoardDto> CreateBoardAsync(string name)
    {
        var b = new BoardDto(_nextBoard++, CheckName(name, null), 0, 0, DateTime.UtcNow, 1);
        _boards.Add(b);
        _byBoard[b.Id] = new();
        BoardsChanged?.Invoke();
        return Task.FromResult(b);
    }

    public Task<BoardDto> RenameBoardAsync(int id, string name, long? expectedVersion)
    {
        var i = _boards.FindIndex(b => b.Id == id);
        if (i < 0) throw new ApiException("Board not found.", System.Net.HttpStatusCode.NotFound);
        if (expectedVersion is { } v && v != _boards[i].Version) throw new ApiException("The board was changed elsewhere. Reload and retry.", System.Net.HttpStatusCode.Conflict);
        _boards[i] = _boards[i] with { Name = CheckName(name, id), Version = _boards[i].Version + 1 };
        BoardsChanged?.Invoke();
        return Task.FromResult(Counted()[i]);
    }

    public Task DeleteBoardAsync(int id)
    {
        if (_boards.Count <= 1) throw new ApiException("The last remaining board cannot be deleted.", System.Net.HttpStatusCode.Conflict);
        var open = (_byBoard.GetValueOrDefault(id) ?? []).Count(t => t.Lane != BoardLane.Done);
        if (open > 0) throw new ApiException($"This board still has {open} tasks that are not done.", System.Net.HttpStatusCode.Conflict);
        _boards.RemoveAll(b => b.Id == id);
        _byBoard.Remove(id);
        BoardsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskVm>> GetBoard(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskVm>>(_tasks.Where(t => !t.Archived).ToList());

    public int ArchivedCount => _tasks.Count(t => t.Archived);

    public TaskVm? Find(int id) => _tasks.Find(t => t.Id == id);

    public Task<TaskVm?> LoadTask(int id, CancellationToken ct) => Task.FromResult(Find(id));

    /// <summary>Tasks per archive page (the server's is 50); tests make it small.</summary>
    public int ArchivePageSize { get; set; } = 50;

    /// <summary>The archive of a board, newest first. The cursor is a plain offset here; the server's is a keyset cursor.</summary>
    public Task<ArchivePageVm> GetArchivePage(int boardId, string? query, string? cursor, CancellationToken ct)
    {
        var all = (_byBoard.GetValueOrDefault(boardId) ?? []).Where(t => t.Archived && (string.IsNullOrWhiteSpace(query) || Matches(t, query.Trim())))
            .OrderByDescending(t => t.DoneAt ?? t.UpdatedAt).ThenByDescending(t => t.Id).ToList();
        var skip = int.TryParse(cursor, out var n) ? n : 0;
        var page = all.Skip(skip).Take(ArchivePageSize).Select(ToArchived).ToList();
        var next = skip + page.Count < all.Count ? (skip + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new ArchivePageVm(page, next));
    }

    public Task<BoardSearchVm> SearchBoard(string query, CancellationToken ct)
    {
        var q = query.Trim();
        var archived = _tasks.Where(t => t.Archived && Matches(t, q)).OrderByDescending(t => t.DoneAt ?? t.UpdatedAt).ThenByDescending(t => t.Id).ToList();
        return Task.FromResult(new BoardSearchVm(q, _tasks.Where(t => !t.Archived && Matches(t, q)).Select(t => t.Id).ToHashSet(),
            archived.Take(10).Select(ToArchived).ToList(), archived.Count > 10));
    }

    private static bool Matches(TaskVm t, string q) =>
        t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
        || t.Fields.Any(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Body.Contains(q, StringComparison.OrdinalIgnoreCase))
        || t.Comments.Any(c => c.Body.Contains(q, StringComparison.OrdinalIgnoreCase))
        || t.Subtasks.Any(s => s.Title.Contains(q, StringComparison.OrdinalIgnoreCase));

    private static ArchivedTaskVm ToArchived(TaskVm t) => new(t.Id, t.Title, t.DoneAt ?? t.UpdatedAt, t.Assignee, t.CommentCount, t.FieldCount);

    /// <summary>Puts an archived task (Done longer than the archive window) on the current board, for tests and the demo.</summary>
    public TaskVm AddArchived(int id, string title, DateTimeOffset doneAt, string description = "")
    {
        var t = new TaskVm(id, title, description, BoardLane.Done, AssigneeVm.UserVm, null, false, false, true,
            UserName, doneAt.AddDays(-1), doneAt, UserName, [], [], [], DoneAt: doneAt);
        _tasks.Add(t);
        return t;
    }

    public Task<PresenceVm> GetPresenceAsync() =>
        Task.FromResult(new PresenceVm(2, "Hermes", DateTimeOffset.Now.AddMinutes(-2)));

    public Task<TaskVm> CreateTaskAsync(string title)
    {
        var now = DateTimeOffset.Now;
        var t = new TaskVm(_nextTask++, title.Trim(), "", BoardLane.New, AssigneeVm.Unassigned, null, false, false, false,
            Fmt.HumanServerName, now, now, Fmt.HumanServerName, [], [], []);
        var firstNew = _tasks.FindIndex(x => x.Lane == BoardLane.New);
        _tasks.Insert(firstNew < 0 ? 0 : firstNew, t);
        return Done(t);
    }

    public Task SetTitleAsync(int id, string title, long? baseVersion = null) => Edit(id, t => t with { Title = title.Trim() });
    public Task SetDescriptionAsync(int id, string description, long? baseVersion = null) => Edit(id, t => t with { Description = description });
    public Task SetDueAsync(int id, DateOnly? due, long? baseVersion = null) => Edit(id, t => t with { Due = due });
    // As on the server: a New task given to an agent or the pool is theirs now and waits for them.
    public Task AssignAsync(int id, AssigneeVm assignee) => Edit(id, t => t with
    {
        Assignee = assignee,
        Lane = t.Lane == BoardLane.New && assignee.Kind is AssigneeKind.Agent or AssigneeKind.Pool ? BoardLane.Waiting : t.Lane,
    });

    public Task MarkSeenAsync(int id) => Edit(id, t => t with
    {
        Unread = false,
        Comments = t.Comments.Select(c => c with { IsNew = false }).ToList(),
        Fields = t.Fields.Select(f => f with { IsNew = false }).ToList(),
    }, touch: false);

    public Task MoveAsync(int id, BoardLane lane, int? beforeId)
    {
        var i = _tasks.FindIndex(t => t.Id == id);
        if (i < 0) return Task.CompletedTask;
        // Out of Done comes out of the archive too; into Done starts the archive window again.
        var t = _tasks[i] with
        {
            Lane = lane, UpdatedAt = DateTimeOffset.Now, UpdatedBy = Fmt.HumanServerName,
            Archived = _tasks[i].Archived && lane == BoardLane.Done,
            DoneAt = lane != BoardLane.Done ? null : _tasks[i].Lane == BoardLane.Done ? _tasks[i].DoneAt : DateTimeOffset.Now,
        };
        _tasks.RemoveAt(i);
        var at = beforeId is int b && b != id ? _tasks.FindIndex(x => x.Id == b) : -1;
        if (at < 0)
        {
            var last = _tasks.FindLastIndex(x => x.Lane == lane);
            at = last < 0 ? _tasks.Count : last + 1;
        }
        _tasks.Insert(at, t);
        return Done();
    }

    public Task DeleteTaskAsync(int id)
    {
        _tasks.RemoveAll(t => t.Id == id);
        return Done();
    }

    public Task AddSubtaskAsync(int id, string title) => Edit(id, t => t with
    {
        Subtasks = t.Subtasks.Append(new SubtaskVm(_nextChild++, title.Trim(), false, t.Assignee)).ToList(),
    });

    public Task SetSubtaskDoneAsync(int id, int subtaskId, bool done) => Edit(id, t => t with
    {
        Subtasks = t.Subtasks.Select(s => s.Id == subtaskId ? s with { Done = done } : s).ToList(),
    });

    public Task AddFieldAsync(int id, string name, string body) => Edit(id, t => t with
    {
        Fields = t.Fields.Append(new InfoFieldVm(_nextChild++, name.Trim(), body, Fmt.HumanServerName, DateTimeOffset.Now, false)).ToList(),
    });

    public Task UpdateFieldAsync(int id, int fieldId, string name, string body) => Edit(id, t => t with
    {
        Fields = t.Fields.Select(f => f.Id == fieldId
            ? f with { Name = name.Trim(), Body = body, Author = Fmt.HumanServerName, UpdatedAt = DateTimeOffset.Now }
            : f).ToList(),
    });

    public Task AddCommentAsync(int id, string body) => Edit(id, t => t with
    {
        Comments = t.Comments.Append(new CommentVm(_nextChild++, Fmt.HumanServerName, body.Trim(), DateTimeOffset.Now, false)).ToList(),
    });

    // ---- session, setup and administration (in memory) ----

    private bool _needsSetup = true;
    private bool _signedIn;
    private string _password = "";
    private int _nextAgent = 3;
    private readonly List<AgentAdminDto> _agents =
    [
        new(1, "Hermes", "H", true, true, "https://hermes.example/webhooks/doiq", null, true, DateTime.UtcNow.AddDays(-9), DateTime.UtcNow.AddMinutes(-2)),
        new(2, "Hermes 2", "H2", false, true, null, "https://hermes.example/notify", false, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddHours(-5),
            Responsibilities: "Content: newsletters, blog posts and social copy."),
    ];

#pragma warning disable CS0067 // the fake never answers 401
    public event Action? Unauthorized;
#pragma warning restore CS0067

    private static string RandomKey() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public Task<SetupStatusDto> GetSetupStatusAsync() => Task.FromResult(new SetupStatusDto(_needsSetup));

    public Task SetupAsync(string password)
    {
        if (!_needsSetup) throw new ApiException("Setup is already done.");
        if (password.Length < 8) throw new ApiException("Use at least 8 characters.");
        _password = password;
        _needsSetup = false;
        _signedIn = true;
        return Task.CompletedTask;
    }

    public Task<bool> IsSignedInAsync() => Task.FromResult(_signedIn);

    public Task LoginAsync(string password)
    {
        if (password != _password) throw new ApiException("Wrong password.");
        _signedIn = true;
        return Task.CompletedTask;
    }

    public Task LogoutAsync() { _signedIn = false; return Task.CompletedTask; }

    public Task ChangePasswordAsync(string current, string newPassword)
    {
        if (current != _password) throw new ApiException("Current password is wrong.");
        if (newPassword.Length < 8) throw new ApiException("Use at least 8 characters.");
        _password = newPassword;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentAdminDto>> GetAgentsAsync() => Task.FromResult<IReadOnlyList<AgentAdminDto>>(_agents.ToList());

    public Task<CreatedAgentResult> CreateAgentAsync(CreateAgentRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ApiException("Name is required.");
        var a = new AgentAdminDto(_nextAgent++, r.Name.Trim(), string.IsNullOrWhiteSpace(r.Initials) ? AssigneeVm.Initials(r.Name) : r.Initials.Trim(),
            r.IsTriage, true, Blank(r.WebhookUrl), Blank(r.NotifyUrl), !string.IsNullOrEmpty(r.WebhookSecret), DateTime.UtcNow, null, r.IsDispatcher, Blank(r.Responsibilities));
        if (r.IsDispatcher) ClearOtherDispatchers(a.Id);
        _agents.Add(a);
        return Task.FromResult(new CreatedAgentResult(a, RandomKey(), Blank(r.WebhookSecret)));
    }

    public Task<AgentAdminDto> UpdateAgentAsync(int id, UpdateAgentRequest r)
    {
        var i = _agents.FindIndex(a => a.Id == id);
        if (i < 0) throw new ApiException("Agent not found.");
        var o = _agents[i];
        _agents[i] = o with
        {
            Name = r.Name.Trim(), Initials = string.IsNullOrWhiteSpace(r.Initials) ? AssigneeVm.Initials(r.Name) : r.Initials.Trim(),
            IsTriage = r.IsTriage, IsDispatcher = r.IsDispatcher, Responsibilities = Blank(r.Responsibilities), WebhookUrl = Blank(r.WebhookUrl), NotifyUrl = Blank(r.NotifyUrl),
            HasWebhookSecret = o.HasWebhookSecret || !string.IsNullOrWhiteSpace(r.WebhookSecret),
        };
        if (r.IsDispatcher) ClearOtherDispatchers(id);
        return Task.FromResult(_agents[i]);
    }

    /// <summary>As on the server: at most one dispatcher.</summary>
    private void ClearOtherDispatchers(int keepId)
    {
        for (var i = 0; i < _agents.Count; i++)
            if (_agents[i].Id != keepId && _agents[i].IsDispatcher) _agents[i] = _agents[i] with { IsDispatcher = false };
    }

    /// <summary>The asks the fake received, newest last (the fake has no agents to answer them).</summary>
    public List<int> ResponsibilityAsks { get; } = [];

    public Task AskForResponsibilities(int agentId, CancellationToken ct)
    {
        ResponsibilityAsks.Add(agentId);
        return Task.CompletedTask;
    }

    public Task<RotatedKeyResult> RotateAgentKeyAsync(int id) => Task.FromResult(new RotatedKeyResult(RandomKey()));

    public Task SetAgentActiveAsync(int id, bool active)
    {
        var i = _agents.FindIndex(a => a.Id == id);
        if (i >= 0) _agents[i] = _agents[i] with { IsActive = active };
        return Task.CompletedTask;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private Task Edit(int id, Func<TaskVm, TaskVm> change, bool touch = true)
    {
        var i = _tasks.FindIndex(t => t.Id == id);
        if (i < 0) return Task.CompletedTask;
        var t = change(_tasks[i]);
        if (touch) t = t with { UpdatedAt = DateTimeOffset.Now, UpdatedBy = Fmt.HumanServerName };
        _tasks[i] = t;
        return Done();
    }

    private Task Done() { Changed?.Invoke(); return Task.CompletedTask; }
    private Task<TaskVm> Done(TaskVm t) { Changed?.Invoke(); return Task.FromResult(t); }

    // ---- seed data (mirrors project/Main.dc.html and Task.dc.html) ----

    private static readonly AssigneeVm Hermes = AssigneeVm.ForAgent("Hermes");
    private static readonly AssigneeVm Hermes2 = AssigneeVm.ForAgent("Hermes 2");
    private static readonly AssigneeVm User = AssigneeVm.UserVm;
    private const string UserName = Fmt.HumanServerName;

    private void Seed()
    {
        var now = DateTimeOffset.Now;
        var today = DateTimeOffset.Now.Date;
        DateTimeOffset At(int h, int m) => new(today.AddHours(h).AddMinutes(m), DateTimeOffset.Now.Offset);
        DateOnly D(int month, int day) => new(2026, month, day);

        // New
        Add(1, "Set up a nightly backup of todo.db", BoardLane.New, AssigneeVm.Unassigned, created: UserName);
        Add(2, "Pick a drag-and-drop approach for the board", BoardLane.New, Hermes, comments: Comments("Hermes", 4), created: UserName);
        Add(3, "Draft the abstract for the AI limits talk", BoardLane.New, User, due: D(11, 3), created: UserName);

        // Waiting
        _tasks.Add(new TaskVm(42, "Reply to the venue about the spring event",
            "Confirm the date and headcount for the spring event, and say whether we need **AV in both rooms**.",
            BoardLane.Waiting, User, D(10, 27), true, true, false, "Hermes", At(9, 12), now.AddMinutes(-2), "Hermes",
            [
                new(1, "Summarise the venue's email", true, Hermes),
                new(2, "Draft the reply", true, Hermes),
                new(3, "Fill in the headcount", false, User),
                new(4, "Send the reply", false, User),
            ],
            [
                new(1, "Email summary", "The venue wants the date and headcount confirmed by 27 Oct. They also ask whether we need AV in one room or two.", "Hermes", At(9, 14), false),
                new(2, "Notes", "Ask whether they have a second projector.", UserName, At(10, 2), false),
                new(3, "Reply draft", "Hi, thanks for checking in. We'd like to keep the date as planned, with [HEADCOUNT] attendees. We need AV in both rooms, and could you tell us whether a second projector is available?", "Hermes", now.AddMinutes(-2), true),
            ],
            [
                new(1, "Hermes", "I made this from the venue's email and gave it to you. Want me to draft the reply?", At(9, 14), false),
                new(2, UserName, "Yes please. Leave the headcount blank, I'll fill it in.", At(10, 3), false),
                new(3, "Hermes", "Draft is in the Reply draft field. Move this to Doing when you send it, or give it back to me if you want changes.", now.AddMinutes(-2), true),
            ]));
        Add(5, "Renew the kopplat.se domain", BoardLane.Waiting, User, due: D(11, 12), fields: Fields(1), unread: true, created: "Hermes");
        Add(6, "Check the Telerik licence renewal date", BoardLane.Waiting, User, email: true, fields: Fields(1), created: "Hermes");
        Add(7, "Collect CFP deadlines for spring conferences", BoardLane.Waiting, AssigneeVm.AnyAgent, created: UserName);
        Add(8, "List NuGet packages that need .NET 10 updates", BoardLane.Waiting, new AssigneeVm(AssigneeKind.Pool, ClaimedBy: "Hermes 2"),
            subs: Subs(1, 3, Hermes2), created: UserName);

        // Doing
        Add(9, "Summarise the Hermes webhook docs", BoardLane.Doing, Hermes2, fields: Fields(2), comments: Comments("Hermes 2", 1), created: UserName);
        Add(10, "Port the login page to MAUI", BoardLane.Doing, User, due: D(10, 30), subs: Subs(1, 5, User), comments: Comments(UserName, 2), created: UserName);

        // Done (recent, then archived: done more than 14 days ago)
        Add(11, "Create the Docker volume on the NAS", BoardLane.Done, User, ago: TimeSpan.FromDays(2), created: UserName);
        Add(12, "List .NET 10 breaking changes for the MAUI port", BoardLane.Done, Hermes, fields: Fields(1), comments: Comments("Hermes", 2), ago: TimeSpan.FromDays(3), created: UserName);
        Add(13, "Update the Docker base image", BoardLane.Done, Hermes, ago: TimeSpan.FromDays(5), created: UserName);
        foreach (var (id, title) in new[] { (14, "Pick a name for the project"), (15, "Sketch the lane layout"), (16, "Choose SQLite over Postgres"), (17, "Write the build plan") })
            Add(id, title, BoardLane.Done, User, ago: TimeSpan.FromDays(20 + id), archived: true, created: UserName);
        foreach (var (id, title) in new[] { (18, "Try the old kanban app"), (19, "Evaluate hosted boards") })
            Add(id, title, BoardLane.Done, User, ago: TimeSpan.FromDays(80 + id), archived: true, created: UserName);
    }

    private void Add(int id, string title, BoardLane lane, AssigneeVm who, DateOnly? due = null, bool email = false,
        bool unread = false, bool archived = false, string created = UserName, TimeSpan? ago = null,
        List<SubtaskVm>? subs = null, List<InfoFieldVm>? fields = null, List<CommentVm>? comments = null)
    {
        var at = DateTimeOffset.Now - (ago ?? TimeSpan.FromHours(id % 7 + 1));
        _tasks.Add(new TaskVm(id, title, "", lane, who, due, email, unread, archived, created, at, at, created,
            subs ?? [], fields ?? [], comments ?? [], DoneAt: lane == BoardLane.Done ? at : null));
    }

    private static List<SubtaskVm> Subs(int done, int total, AssigneeVm who) =>
        Enumerable.Range(1, total).Select(i => new SubtaskVm(i, $"Step {i}", i <= done, who)).ToList();

    private static List<InfoFieldVm> Fields(int n) =>
        Enumerable.Range(1, n).Select(i => new InfoFieldVm(i, i == 1 ? "Notes" : $"Field {i}", "Details go here.", "Hermes", DateTimeOffset.Now.AddHours(-i), false)).ToList();

    private static List<CommentVm> Comments(string author, int n) =>
        Enumerable.Range(1, n).Select(i => new CommentVm(i, i % 2 == 1 ? author : UserName, $"Comment {i}.", DateTimeOffset.Now.AddHours(-n + i), false)).ToList();
}

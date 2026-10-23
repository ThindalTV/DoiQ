using DoiQ.Core.Data;
using DoiQ.Core.Options;
using DoiQ.Core.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoiQ.Tests;

public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class TestActor : ICurrentActor
{
    public ActorInfo? Current { get; set; }
}

public sealed class RecordingBroadcaster : IBoardBroadcaster
{
    public List<TaskChangedMessage> Changed { get; } = [];
    public List<TaskDeletedMessage> Deleted { get; } = [];
    public List<CommentAddedMessage> Comments { get; } = [];
    public List<FieldChangedMessage> Fields { get; } = [];
    public List<SeenChangedMessage> Seen { get; } = [];
    public List<BoardChangedMessage> BoardChanges { get; } = [];
    public Task SeenChangedAsync(SeenChangedMessage m) { Seen.Add(m); return Task.CompletedTask; }
    public Task BoardChangedAsync(BoardChangedMessage m) { BoardChanges.Add(m); return Task.CompletedTask; }
    public Func<long>? OnPublishCheckDb { get; set; }
    public Task TaskChangedAsync(TaskChangedMessage m) { OnPublishCheckDb?.Invoke(); Changed.Add(m); return Task.CompletedTask; }
    public Task TaskDeletedAsync(TaskDeletedMessage m) { Deleted.Add(m); return Task.CompletedTask; }
    public Task CommentAddedAsync(CommentAddedMessage m) { Comments.Add(m); return Task.CompletedTask; }
    public Task FieldChangedAsync(FieldChangedMessage m) { Fields.Add(m); return Task.CompletedTask; }
}

/// <summary>In-memory SQLite shared by every context/service the test creates (one "request" = one new service).</summary>
public sealed class Harness : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<DoiQDbContext> _options;
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    public RecordingBroadcaster Broadcaster { get; } = new();
    public BoardOptions Board { get; } = new();

    public ActorInfo User { get; private set; } = null!;
    public ActorInfo Hermes { get; private set; } = null!;   // triage agent
    public ActorInfo Odin { get; private set; } = null!;     // second agent

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    /// <summary>The token every call in a test passes on; it cancels after two minutes so a hung test cannot block the run.</summary>
    public CancellationToken Ct => _cts.Token;

    public Harness()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<DoiQDbContext>().UseSqlite(_conn).Options;
        using (var db = NewDb()) db.Database.Migrate();
        using (var db = NewDb())
        {
            new ActorService(db).Seed(new AuthOptions(), [
                new AgentSeed("Hermes", "H", "hermes-key", IsTriage: true),
                new AgentSeed("Odin", "O", "odin-key", IsTriage: false),
            ], _cts.Token).GetAwaiter().GetResult();
        }
        using var db2 = NewDb();
        var actors = db2.Actors.AsNoTracking().ToList().Select(ActorService.ToInfo).ToList();
        User = actors.Single(a => a.IsHuman);
        Hermes = actors.Single(a => a.Name == "Hermes");
        Odin = actors.Single(a => a.Name == "Odin");
    }

    public DoiQDbContext NewDb() => new(_options, Clock, Options.Create(Board));

    public TaskService As(ActorInfo actor, ICurrentActor? _ = null) =>
        new(NewDb(), new TestActor { Current = actor }, Broadcaster, new NoopOutboxSignal(), Clock, Options.Create(Board), NullLogger<TaskService>.Instance);

    public BoardService BoardsAs(ActorInfo actor) =>
        new(NewDb(), new TestActor { Current = actor }, Broadcaster, Clock, NullLogger<BoardService>.Instance);

    public BoardService BoardsAsUser() => BoardsAs(User);

    /// <summary>The id of the board every fresh database starts with.</summary>
    public int MainBoardId => NewDb().Boards.AsNoTracking().OrderBy(b => b.Id).First().Id;

    public async Task<int> AddBoardAsync(string name) => (await BoardsAsUser().CreateAsync(new CreateBoardRequest(name))).Id;

    public async Task<int> CreateOnAsync(int boardId, string title, BoardStatus? status = null, string? assignee = null)
    {
        var r = await AsUser().Create(new CreateTaskRequest(title, Status: status, Assignee: assignee, BoardId: boardId));
        return r.Task.Task.Id;
    }

    public TaskService AsUser() => As(User);
    public TaskService AsHermes() => As(Hermes);
    public TaskService AsOdin() => As(Odin);

    public async Task<int> CreateAsync(ActorInfo by, string title, BoardStatus? status = null, string? assignee = null)
    {
        var r = await As(by).Create(new CreateTaskRequest(title, Status: status, Assignee: assignee));
        return r.Task.Task.Id;
    }

    public void Dispose()
    {
        _conn.Dispose();
        _cts.Dispose();
    }
}

using System.Net.Http.Json;
using DoiQ.Client.Models;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace DoiQ.Tests;

/// <summary>The browser's data layer with several boards: it shows one, applies only that board's pushes and follows board changes.</summary>
[Collection("Http")]
public sealed class BoardClientTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HubConnection> _connections = [];
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    /// <summary>The token the client calls pass on; it cancels after two minutes so a hung test cannot block the run.</summary>
    private CancellationToken Ct => _cts.Token;

    public BoardClientTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
    }

    private sealed record Setup(DoiQApiClient Browser, HttpClient UserHttp, HttpClient Agent, string Cookie, int MainId, int WorkId);

    /// <summary>A signed-in browser client, an agent client and two boards (Main and Work).</summary>
    private async Task<Setup> ArrangeAsync()
    {
        var userHttp = _factory.CreateClient();
        var setup = await userHttp.PostAsJsonAsync("/api/setup", new SetupRequest(Password));
        setup.EnsureSuccessStatusCode();
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth=")).Split(';')[0];
        var created = await (await userHttp.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Loki", IsTriage: true))).Content.ReadFromJsonAsync<CreatedAgentResult>();
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        var browser = new DoiQApiClient(userHttp);
        var work = await browser.CreateBoardAsync("Work");
        var main = (await browser.GetBoardsAsync()).Single(b => b.Name == "Main");
        return new Setup(browser, userHttp, agent, cookie, main.Id, work.Id);
    }

    private async Task ConnectAsync(IBoardSync sync, string cookie)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubEvents.HubPath), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        connection.On<TaskChangedMessage>(BoardHubEvents.TaskChanged, m => sync.ApplyAsync(m));
        connection.On<TaskDeletedMessage>(BoardHubEvents.TaskDeleted, m => sync.ApplyAsync(m));
        connection.On<CommentAddedMessage>(BoardHubEvents.CommentAdded, m => sync.ApplyAsync(m));
        connection.On<FieldChangedMessage>(BoardHubEvents.FieldChanged, m => sync.ApplyAsync(m));
        connection.On<BoardChangedMessage>(BoardHubEvents.BoardChanged, m => sync.ApplyAsync(m));
        _connections.Add(connection);
        await connection.StartAsync();
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    private static async Task<int> CreateAsync(HttpClient who, int boardId, string title) =>
        (await (await who.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title, BoardId: boardId))).Content.ReadFromJsonAsync<TaskDetailDto>())!.Task.Id;

    private async Task<List<string>> TitlesAsync(DoiQApiClient browser) => (await browser.GetBoard(Ct)).Select(t => t.Title).ToList();

    [Fact]
    public async Task DoiQApiClient_SelectingABoard_LoadsOnlyThatBoardsTasks()
    {
        // Arrange
        var s = await ArrangeAsync();
        await CreateAsync(s.UserHttp, s.MainId, "On Main");
        await CreateAsync(s.UserHttp, s.WorkId, "On Work");

        // Act
        await s.Browser.SelectBoardAsync(s.WorkId);
        var titles = await TitlesAsync(s.Browser);

        // Assert
        Assert.Equal(["On Work"], titles);
        Assert.Equal(s.WorkId, s.Browser.CurrentBoardId);
    }

    [Fact]
    public async Task DoiQApiClient_SwitchingBoards_ShowsTheOtherBoardsTasksAndNotTheFirst()
    {
        // Arrange
        var s = await ArrangeAsync();
        await CreateAsync(s.UserHttp, s.MainId, "On Main");
        await CreateAsync(s.UserHttp, s.WorkId, "On Work");
        await s.Browser.SelectBoardAsync(s.MainId);
        await TitlesAsync(s.Browser);

        // Act
        await s.Browser.SelectBoardAsync(s.WorkId);
        var titles = await TitlesAsync(s.Browser);

        // Assert
        Assert.Equal(["On Work"], titles);
    }

    [Fact]
    public async Task DoiQApiClient_PushForAnotherBoard_LeavesTheShownBoardAlone()
    {
        // Arrange
        var s = await ArrangeAsync();
        await s.Browser.SelectBoardAsync(s.MainId);
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);
        var changes = 0;
        s.Browser.Changed += () => changes++;

        // Act
        await CreateAsync(s.Agent, s.WorkId, "Hidden elsewhere");
        var sentinel = await CreateAsync(s.Agent, s.MainId, "Visible here");   // pushes arrive in order, so once this shows the other one has been seen too

        // Assert
        Assert.True(await EventuallyAsync(async () => (await s.Browser.GetBoard(Ct)).Any(t => t.Id == sentinel)));
        Assert.Equal(["Visible here"], await TitlesAsync(s.Browser));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task DoiQApiClient_PushForTheShownBoard_AppliesAfterSwitchingToIt()
    {
        // Arrange
        var s = await ArrangeAsync();
        await s.Browser.SelectBoardAsync(s.MainId);
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);
        await s.Browser.SelectBoardAsync(s.WorkId);
        await s.Browser.GetBoard(Ct);

        // Act
        var id = await CreateAsync(s.Agent, s.WorkId, "Arrives live");

        // Assert
        Assert.True(await EventuallyAsync(async () => (await s.Browser.GetBoard(Ct)).Any(t => t.Id == id)));
    }

    [Fact]
    public async Task DoiQApiClient_CatchUp_FetchesOnlyTheShownBoardsMissedEvents()
    {
        // Arrange
        var s = await ArrangeAsync();
        await s.Browser.SelectBoardAsync(s.MainId);
        await s.Browser.GetBoard(Ct);   // no hub connected: these changes are "missed"
        await CreateAsync(s.Agent, s.WorkId, "Missed on Work");
        await CreateAsync(s.Agent, s.MainId, "Missed on Main");

        // Act
        await s.Browser.CatchUpAsync();

        // Assert
        Assert.Equal(["Missed on Main"], await TitlesAsync(s.Browser));
    }

    [Fact]
    public async Task DoiQApiClient_CreatingATask_PutsItOnTheShownBoard()
    {
        // Arrange
        var s = await ArrangeAsync();
        await s.Browser.SelectBoardAsync(s.WorkId);
        await s.Browser.GetBoard(Ct);

        // Act
        var created = await s.Browser.CreateTaskAsync("Quick add");

        // Assert
        var onWork = await s.UserHttp.GetFromJsonAsync<List<TaskSummaryDto>>($"/api/tasks?boardId={s.WorkId}");
        Assert.Equal([created.Id], onWork!.Select(t => t.Id));
    }

    [Fact]
    public async Task DoiQApiClient_BoardAddedInAnotherTab_RaisesBoardsChangedAndUpdatesTheList()
    {
        // Arrange
        var s = await ArrangeAsync();
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);
        var raised = 0;
        s.Browser.BoardsChanged += () => raised++;

        // Act
        var otherTab = new DoiQApiClient(s.UserHttp);
        await otherTab.CreateBoardAsync("Home");

        // Assert
        Assert.True(await EventuallyAsync(() => Task.FromResult(s.Browser.Boards.Any(b => b.Name == "Home"))));
        Assert.True(raised >= 1);
    }

    [Fact]
    public async Task DoiQApiClient_DeletingABoardWithAnOpenTask_ThrowsTheServersMessage()
    {
        // Arrange
        var s = await ArrangeAsync();
        await CreateAsync(s.UserHttp, s.WorkId, "Still open");

        // Act
        var ex = await Record.ExceptionAsync(() => s.Browser.DeleteBoardAsync(s.WorkId));

        // Assert
        var api = Assert.IsType<ApiException>(ex);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, api.Status);
        Assert.Contains("1 task", api.Message);
    }

    // ───────────────────────── moving a task to another board ─────────────────────────

    private static async Task MoveToBoardAsync(HttpClient who, int taskId, int boardId) =>
        (await who.PostAsJsonAsync($"/api/tasks/{taskId}/move-to-board", new MoveToBoardRequest(boardId))).EnsureSuccessStatusCode();

    [Fact]
    public async Task DoiQApiClient_MoveToBoard_TakesTheTaskOffTheShownBoardAndUpdatesTheCounts()
    {
        // Arrange
        var s = await ArrangeAsync();
        var id = await CreateAsync(s.UserHttp, s.MainId, "Moving");
        await s.Browser.SelectBoardAsync(s.MainId);
        await s.Browser.GetBoard(Ct);

        // Act
        await s.Browser.MoveToBoard(id, s.WorkId, Ct);

        // Assert
        Assert.Empty(await TitlesAsync(s.Browser));
        Assert.Equal(1, s.Browser.Boards.Single(b => b.Id == s.WorkId).OpenTasks);
        Assert.Equal(0, s.Browser.Boards.Single(b => b.Id == s.MainId).OpenTasks);
    }

    [Fact]
    public async Task DoiQApiClient_PushOfTaskMovedAway_RemovesItFromTheShownBoard()
    {
        // Arrange
        var s = await ArrangeAsync();
        var id = await CreateAsync(s.UserHttp, s.MainId, "Moving");
        await s.Browser.SelectBoardAsync(s.MainId);
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);

        // Act
        await MoveToBoardAsync(s.Agent, id, s.WorkId);

        // Assert
        Assert.True(await EventuallyAsync(async () => (await s.Browser.GetBoard(Ct)).Count == 0));
    }

    [Fact]
    public async Task DoiQApiClient_PushOfTaskMovedHere_AddsItWithoutTouchingTheArchivedCount()
    {
        // Arrange
        var s = await ArrangeAsync();
        var id = await CreateAsync(s.UserHttp, s.MainId, "Arriving");
        await s.Browser.SelectBoardAsync(s.WorkId);
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);
        var archived = s.Browser.ArchivedCount;

        // Act
        await MoveToBoardAsync(s.Agent, id, s.WorkId);

        // Assert
        Assert.True(await EventuallyAsync(async () => (await s.Browser.GetBoard(Ct)).Any(t => t.Id == id)));
        Assert.Equal(archived, s.Browser.ArchivedCount);
    }

    [Fact]
    public async Task DoiQApiClient_CatchUpAfterTaskMovedAway_RemovesItAndTheOtherBoardGetsIt()
    {
        // Arrange
        var s = await ArrangeAsync();
        var id = await CreateAsync(s.UserHttp, s.MainId, "Moving");
        await s.Browser.SelectBoardAsync(s.MainId);
        await s.Browser.GetBoard(Ct);
        var work = new DoiQApiClient(s.UserHttp);
        await work.SelectBoardAsync(s.WorkId);
        await work.GetBoard(Ct);

        // Act
        await MoveToBoardAsync(s.Agent, id, s.WorkId);
        await s.Browser.CatchUpAsync();
        await work.CatchUpAsync();

        // Assert
        Assert.Empty(await TitlesAsync(s.Browser));
        Assert.Equal(["Moving"], await TitlesAsync(work));
    }

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _cts.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch { } }
    }
}

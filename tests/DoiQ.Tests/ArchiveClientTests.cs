using System.Net.Http.Json;
using DoiQ.Client.Models;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;

namespace DoiQ.Tests;

/// <summary>
/// The browser's data layer against a real server whose archive window is 0 days: a task is archived the moment it is
/// in Done, so archiving needs no clock. The board holds active tasks only and an archived task never comes back unless
/// it leaves Done.
/// </summary>
[Collection("Http")]
public sealed class ArchiveClientTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HubConnection> _connections = [];
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    /// <summary>The token the client calls pass on; it cancels after two minutes so a hung test cannot block the run.</summary>
    private CancellationToken Ct => _cts.Token;

    public ArchiveClientTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Board:ArchiveAfterDays"] = "0" }));
        });
    }

    /// <summary>Counts the requests the browser's HttpClient sends.</summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record Setup(DoiQApiClient Browser, HttpClient UserHttp, HttpClient Agent, string Cookie, CountingHandler Requests);

    private async Task<Setup> ArrangeAsync()
    {
        var counter = new CountingHandler();
        var userHttp = _factory.CreateDefaultClient(new CookieContainerHandler(), counter);
        var setup = await userHttp.PostAsJsonAsync("/api/setup", new SetupRequest(Password), Ct);
        setup.EnsureSuccessStatusCode();
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth=")).Split(';')[0];
        var created = await (await userHttp.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Loki", IsTriage: true), Ct)).Content.ReadFromJsonAsync<CreatedAgentResult>(Ct);
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        return new Setup(new DoiQApiClient(userHttp), userHttp, agent, cookie, counter);
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
        _connections.Add(connection);
        await connection.StartAsync(Ct);
    }

    private async Task<int> CreateAsync(HttpClient who, string title, BoardStatus? status = null) =>
        (await (await who.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title, Status: status), Ct)).Content.ReadFromJsonAsync<TaskDetailDto>(Ct))!.Task.Id;

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    private async Task<bool> OnBoardAsync(DoiQApiClient browser, int id) => (await browser.GetBoard(Ct)).Any(t => t.Id == id);

    [Fact]
    public async Task DoiQApiClient_BoardLoad_SendsFourRequestsWhateverTheNumberOfTasks()
    {
        // Arrange
        var s = await ArrangeAsync();
        for (var i = 0; i < 12; i++)
        {
            var id = await CreateAsync(s.UserHttp, $"open {i}");
            (await s.UserHttp.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("note"), Ct)).EnsureSuccessStatusCode();
        }
        for (var i = 0; i < 5; i++) await CreateAsync(s.UserHttp, $"old {i}", BoardStatus.Done);
        var before = s.Requests.Count;

        // Act
        var board = await s.Browser.GetBoard(Ct);

        // Assert
        Assert.Equal(4, s.Requests.Count - before);   // boards, me, actors, the board load: no request per card
        Assert.Equal(12, board.Count);
        Assert.Equal(5, s.Browser.ArchivedCount);
        Assert.All(board, t => Assert.Equal(1, t.CommentCount));
        Assert.All(board, t => Assert.Empty(t.Comments));   // the drawer loads the lists on open
    }

    [Fact]
    public async Task DoiQApiClient_LoadTask_BringsTheListsTheDrawerShows()
    {
        // Arrange
        var s = await ArrangeAsync();
        var id = await CreateAsync(s.UserHttp, "talk");
        (await s.Agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("hello"), Ct)).EnsureSuccessStatusCode();
        await s.Browser.GetBoard(Ct);

        // Act
        var task = await s.Browser.LoadTask(id, Ct);

        // Assert
        Assert.Equal(["hello"], task!.Comments.Select(c => c.Body));
        Assert.True(task.Comments[0].IsNew);
    }

    [Fact]
    public async Task DoiQApiClient_TaskMovedToDone_LeavesTheBoardForTheArchive()
    {
        // Arrange
        var s = await ArrangeAsync();
        var id = await CreateAsync(s.UserHttp, "finish me");
        await s.Browser.GetBoard(Ct);

        // Act
        await s.Browser.MoveAsync(id, BoardLane.Done, null);

        // Assert
        Assert.False(await OnBoardAsync(s.Browser, id));
        Assert.Equal(1, s.Browser.ArchivedCount);
    }

    [Fact]
    public async Task DoiQApiClient_CommentPushedForAnArchivedTask_DoesNotPutItBackOnTheBoard()
    {
        // Arrange
        var s = await ArrangeAsync();
        var archived = await CreateAsync(s.UserHttp, "old", BoardStatus.Done);
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);

        // Act
        (await s.Agent.PostAsJsonAsync($"/api/tasks/{archived}/comments", new AddCommentRequest("late note"), Ct)).EnsureSuccessStatusCode();
        var sentinel = await CreateAsync(s.Agent, "after the comment");

        // Assert
        Assert.True(await EventuallyAsync(() => OnBoardAsync(s.Browser, sentinel)));
        Assert.False(await OnBoardAsync(s.Browser, archived));
        Assert.Equal(1, s.Browser.ArchivedCount);
    }

    [Fact]
    public async Task DoiQApiClient_ArchivedTaskReopenedElsewhere_AppearsOnTheBoardLive()
    {
        // Arrange
        var s = await ArrangeAsync();
        var archived = await CreateAsync(s.UserHttp, "old", BoardStatus.Done);
        await s.Browser.GetBoard(Ct);
        await ConnectAsync(s.Browser, s.Cookie);

        // Act
        (await s.UserHttp.PostAsJsonAsync($"/api/tasks/{archived}/move", new MoveTaskRequest(BoardStatus.Waiting), Ct)).EnsureSuccessStatusCode();

        // Assert
        Assert.True(await EventuallyAsync(() => OnBoardAsync(s.Browser, archived)));
        Assert.Equal(BoardLane.Waiting, s.Browser.Find(archived)!.Lane);
        Assert.Equal(0, s.Browser.ArchivedCount);
    }

    [Fact]
    public async Task DoiQApiClient_ReopenFromTheArchiveView_PutsTheTaskOnTheBoard()
    {
        // Arrange
        var s = await ArrangeAsync();
        var archived = await CreateAsync(s.UserHttp, "old", BoardStatus.Done);
        await s.Browser.GetBoard(Ct);
        var opened = await s.Browser.LoadTask(archived, Ct);

        // Assume
        Assert.True(opened!.Archived);

        // Act
        await s.Browser.MoveAsync(archived, BoardLane.New, null);

        // Assert
        Assert.True(await OnBoardAsync(s.Browser, archived));
        Assert.Equal(0, s.Browser.ArchivedCount);
    }

    [Fact]
    public async Task DoiQApiClient_ArchivedTaskChangedElsewhereBeforeAMove_ThrowsAConflictWithTheirVersion()
    {
        // Arrange
        var s = await ArrangeAsync();
        var archived = await CreateAsync(s.UserHttp, "old", BoardStatus.Done);
        await s.Browser.LoadTask(archived, Ct);
        (await s.Agent.PatchAsJsonAsync($"/api/tasks/{archived}", new UpdateTaskRequest(Title: "renamed by Loki"), Ct)).EnsureSuccessStatusCode();

        // Act
        var ex = await Record.ExceptionAsync(() => s.Browser.MoveAsync(archived, BoardLane.New, null));

        // Assert
        var conflict = Assert.IsType<TaskConflictException>(ex);
        Assert.Equal("Loki", conflict.ChangedBy);
        Assert.Equal("renamed by Loki", s.Browser.Find(archived)!.Title);
        Assert.False(await OnBoardAsync(s.Browser, archived));
    }

    [Fact]
    public async Task DoiQApiClient_ArchivePages_ListNewestDoneFirstAndContinueWithTheCursor()
    {
        // Arrange
        var s = await ArrangeAsync();
        var ids = new List<int>();
        for (var i = 0; i < 53; i++) ids.Add(await CreateAsync(s.UserHttp, $"old {i}", BoardStatus.Done));
        await s.Browser.GetBoard(Ct);
        var first = await s.Browser.GetArchivePage(s.Browser.CurrentBoardId, null, null, Ct);

        // Act
        var second = await s.Browser.GetArchivePage(s.Browser.CurrentBoardId, null, first.NextCursor, Ct);

        // Assert
        Assert.Equal(50, first.Tasks.Count);
        Assert.Equal(ids[^1], first.Tasks[0].Id);
        Assert.Equal([ids[2], ids[1], ids[0]], second.Tasks.Select(t => t.Id));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task DoiQApiClient_SearchBoard_ReturnsActiveMatchesAndTheArchivedGroup()
    {
        // Arrange
        var s = await ArrangeAsync();
        var archived = await CreateAsync(s.UserHttp, "old", BoardStatus.Done);
        (await s.UserHttp.PostAsJsonAsync($"/api/tasks/{archived}/comments", new AddCommentRequest("about the garden"), Ct)).EnsureSuccessStatusCode();
        var active = await CreateAsync(s.UserHttp, "garden hose");
        await s.Browser.GetBoard(Ct);

        // Act
        var found = await s.Browser.SearchBoard("garden", Ct);

        // Assert
        Assert.Equal([active], found.ActiveIds);
        Assert.Equal([archived], found.Archived.Select(t => t.Id));
        Assert.False(found.MoreArchived);
    }

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _cts.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch (IOException) { } }
    }
}

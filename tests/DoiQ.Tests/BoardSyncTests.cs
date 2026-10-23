using System.Net.Http.Json;
using DoiQ.Client.Models;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace DoiQ.Tests;

/// <summary>The browser's data layer (DoiQApiClient) against a real server: load, pushed events, echoes, catch-up.</summary>
[Collection("Http")]
public sealed class BoardSyncTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HubConnection> _connections = [];
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    /// <summary>The token the client calls pass on; it cancels after two minutes so a hung test cannot block the run.</summary>
    private CancellationToken Ct => _cts.Token;

    public BoardSyncTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
    }

    /// <summary>The user's browser: a client signed in with the cookie, plus another client holding an agent key.</summary>
    private async Task<(DoiQApiClient Browser, HttpClient UserHttp, HttpClient Agent, string Cookie)> ArrangeAsync()
    {
        var userHttp = _factory.CreateClient();
        var setup = await userHttp.PostAsJsonAsync("/api/setup", new SetupRequest(Password));
        setup.EnsureSuccessStatusCode();
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth=")).Split(';')[0];
        var created = await (await userHttp.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Loki", IsTriage: true))).Content.ReadFromJsonAsync<CreatedAgentResult>();
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        return (new DoiQApiClient(userHttp), userHttp, agent, cookie);   // the browser shares the user's cookie jar
    }

    /// <summary>Connects the hub the way BoardHubClient does and feeds every message to the client.</summary>
    private async Task<HubConnection> ConnectAsync(IBoardSync sync, string cookie)
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
        await connection.StartAsync();
        return connection;
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

    private async Task<TaskVm?> TaskAsync(DoiQApiClient browser, int id) => (await browser.GetBoard(Ct)).FirstOrDefault(t => t.Id == id);

    private static async Task<int> CreateAsync(HttpClient who, string title)
    {
        var detail = await (await who.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title))).Content.ReadFromJsonAsync<TaskDetailDto>();
        return detail!.Task.Id;
    }

    [Fact]
    public async Task DoiQApiClient_AgentCreatesATask_AppearsOnTheBoardThroughThePush()
    {
        // Arrange
        var (browser, _, agent, cookie) = await ArrangeAsync();
        await browser.GetBoard(Ct);
        await ConnectAsync(browser, cookie);

        // Act
        var id = await CreateAsync(agent, "From the agent");

        // Assert
        Assert.True(await EventuallyAsync(async () => await TaskAsync(browser, id) is not null));
        var task = await TaskAsync(browser, id);
        Assert.Equal("From the agent", task!.Title);
        Assert.Equal(BoardLane.New, task.Lane);
    }

    [Fact]
    public async Task DoiQApiClient_AgentComments_TheCardGainsTheCommentAndAnUnreadDot()
    {
        // Arrange
        var (browser, _, agent, cookie) = await ArrangeAsync();
        var id = await CreateAsync(agent, "Talk to me");
        await browser.GetBoard(Ct);
        await browser.MarkSeenAsync(id);
        await ConnectAsync(browser, cookie);
        Assert.False((await TaskAsync(browser, id))!.Unread);

        // Act
        (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("done?"))).EnsureSuccessStatusCode();

        // Assert
        Assert.True(await EventuallyAsync(async () => (await TaskAsync(browser, id))!.Comments.Count == 1));
        var task = await TaskAsync(browser, id);
        Assert.True(task!.Unread);
        Assert.True(task.Comments[0].IsNew);
        Assert.Equal("done?", task.Comments[0].Body);
    }

    [Fact]
    public async Task DoiQApiClient_AgentMovesATask_TheCardChangesLane()
    {
        // Arrange
        var (browser, _, agent, cookie) = await ArrangeAsync();
        var id = await CreateAsync(agent, "Move me");
        await browser.GetBoard(Ct);
        await ConnectAsync(browser, cookie);

        // Act
        (await agent.PostAsJsonAsync($"/api/tasks/{id}/assign", new AssignRequest("Loki"))).EnsureSuccessStatusCode();
        (await agent.PostAsJsonAsync($"/api/tasks/{id}/move", new MoveTaskRequest(DoiQ.Shared.Enums.BoardStatus.Doing))).EnsureSuccessStatusCode();

        // Assert
        Assert.True(await EventuallyAsync(async () => (await TaskAsync(browser, id))!.Lane == BoardLane.Doing));
        Assert.Equal("Loki", (await TaskAsync(browser, id))!.Assignee.Agent);
    }

    [Fact]
    public async Task DoiQApiClient_UserDeletesATask_TheCardDisappears()
    {
        // Arrange
        var (browser, userHttp, agent, cookie) = await ArrangeAsync();
        var id = await CreateAsync(agent, "Short lived");
        await browser.GetBoard(Ct);
        await ConnectAsync(browser, cookie);

        // Act
        (await userHttp.DeleteAsync($"/api/tasks/{id}")).EnsureSuccessStatusCode();

        // Assert
        Assert.True(await EventuallyAsync(async () => await TaskAsync(browser, id) is null));
    }

    [Fact]
    public async Task DoiQApiClient_OwnMoveEchoedBackByTheHub_LeavesTheCardInItsNewLaneAndChangesTheBoardAtMostOnce()
    {
        // Arrange
        var (browser, _, _, cookie) = await ArrangeAsync();
        await browser.GetBoard(Ct);
        var task = await browser.CreateTaskAsync("Mine");
        var changes = 0;
        browser.Changed += () => changes++;
        await ConnectAsync(browser, cookie);

        // Act
        await browser.MoveAsync(task.Id, BoardLane.Doing, null);
        await Task.Delay(500);

        // Assert
        // The echo can land before or after the REST answer; whichever comes second finds nothing to change.
        Assert.Equal(BoardLane.Doing, (await TaskAsync(browser, task.Id))!.Lane);
        Assert.InRange(changes, 0, 1);
    }

    [Fact]
    public async Task DoiQApiClient_ApplyingTheSameEventTwice_ChangesTheBoardOnce()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var id = await CreateAsync(agent, "Echo");
        var summary = await browser.LoadTask(id, Ct) ?? throw new InvalidOperationException("The task is gone.");   // opened: its lists are loaded
        var message = new CommentAddedMessage(long.MaxValue / 2, 99, DoiQ.Shared.Enums.ActorKind.Agent, id,
            new CommentDto(500, id, 99, "Loki", "hello", DateTime.UtcNow, null), browser.CurrentBoardId);
        var changes = 0;
        browser.Changed += () => changes++;

        // Act
        await browser.ApplyAsync(message);
        await browser.ApplyAsync(message);

        // Assert
        Assert.Equal(1, changes);
        Assert.Single((await TaskAsync(browser, summary.Id))!.Comments);
    }

    private static CommentAddedMessage Pushed(CommentDto comment, DoiQApiClient browser) =>
        new(comment.EventId, 99, DoiQ.Shared.Enums.ActorKind.Agent, comment.TaskId, comment, browser.CurrentBoardId);

    [Fact]
    public async Task DoiQApiClient_LaterEventPushedBeforeAnEarlierOne_KeepsBothAndNeedsNoCatchUp()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var id = await CreateAsync(agent, "Out of order");
        await browser.GetBoard(Ct);
        browser.CatchUpDelay = TimeSpan.FromMilliseconds(50);
        var first = (await (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("first"))).Content.ReadFromJsonAsync<CommentDto>())!;
        var second = (await (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("second"))).Content.ReadFromJsonAsync<CommentDto>())!;

        // Act
        await browser.ApplyAsync(Pushed(second, browser));
        await browser.ApplyAsync(Pushed(first, browser));
        await Task.Delay(300);

        // Assert
        var bodies = (await TaskAsync(browser, id))!.Comments.Select(c => c.Body).Order().ToList();
        Assert.Equal(["first", "second"], bodies);
    }

    [Fact]
    public async Task DoiQApiClient_EventPushedAheadOfAMissedOne_CatchUpFillsTheGap()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var id = await CreateAsync(agent, "Gap");
        await browser.GetBoard(Ct);
        browser.CatchUpDelay = TimeSpan.FromMilliseconds(50);
        (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("missed"))).EnsureSuccessStatusCode();
        var second = (await (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("pushed"))).Content.ReadFromJsonAsync<CommentDto>())!;

        // Act
        await browser.ApplyAsync(Pushed(second, browser));   // the first comment's event never arrives

        // Assert
        Assert.True(await EventuallyAsync(async () => (await TaskAsync(browser, id))!.Comments.Count == 2));
        Assert.Equal(["missed", "pushed"], (await TaskAsync(browser, id))!.Comments.Select(c => c.Body).Order().ToList());
    }

    [Fact]
    public async Task DoiQApiClient_PushedEventAppliedAgainAfterTheGapIsFilled_ChangesNothing()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var id = await CreateAsync(agent, "No echo twice");
        await browser.GetBoard(Ct);
        browser.CatchUpDelay = TimeSpan.FromMilliseconds(50);
        var comment = (await (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("once"))).Content.ReadFromJsonAsync<CommentDto>())!;
        await browser.CatchUpAsync();
        var changes = 0;
        browser.Changed += () => changes++;

        // Act
        await browser.ApplyAsync(Pushed(comment, browser));

        // Assert
        Assert.Equal(0, changes);
        Assert.Single((await TaskAsync(browser, id))!.Comments);
    }

    [Fact]
    public async Task DoiQApiClient_CatchUpAfterMissedEvents_BringsTheBoardUpToDate()
    {
        // Arrange
        var (browser, userHttp, agent, _) = await ArrangeAsync();
        var kept = await CreateAsync(agent, "Kept");
        var gone = await CreateAsync(agent, "Gone");
        await browser.GetBoard(Ct);   // the hub is not connected from here on: these changes are all missed

        // Act
        var added = await CreateAsync(agent, "Added while away");
        (await agent.PostAsJsonAsync($"/api/tasks/{kept}/comments", new AddCommentRequest("while you were out"))).EnsureSuccessStatusCode();
        (await userHttp.DeleteAsync($"/api/tasks/{gone}")).EnsureSuccessStatusCode();
        await browser.CatchUpAsync();

        // Assert
        var board = await browser.GetBoard(Ct);
        Assert.Contains(board, t => t.Id == added);
        Assert.DoesNotContain(board, t => t.Id == gone);
        Assert.Equal("while you were out", board.Single(t => t.Id == kept).Comments.Single().Body);
    }

    [Fact]
    public async Task DoiQApiClient_CatchUpWithNothingMissed_RaisesNoChange()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        await CreateAsync(agent, "Quiet");
        await browser.GetBoard(Ct);
        var changes = 0;
        browser.Changed += () => changes++;

        // Act
        await browser.CatchUpAsync();

        // Assert
        Assert.Equal(0, changes);
    }

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _cts.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch { /* best effort */ } }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DoiQ.Client.Models;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace DoiQ.Tests;

/// <summary>Stale writes and comment markers end to end: the HTTP answers, then the browser's data layer on top of them.</summary>
[Collection("Http")]
public sealed class ConflictClientTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HubConnection> _connections = [];
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    /// <summary>The token the client calls pass on; it cancels after two minutes so a hung test cannot block the run.</summary>
    private CancellationToken Ct => _cts.Token;

    public ConflictClientTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
    }

    private async Task<(DoiQApiClient Browser, HttpClient UserHttp, HttpClient Agent, string Cookie)> ArrangeAsync()
    {
        var userHttp = _factory.CreateClient();
        var setup = await userHttp.PostAsJsonAsync("/api/setup", new SetupRequest(Password));
        setup.EnsureSuccessStatusCode();
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth=")).Split(';')[0];
        var created = await (await userHttp.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Loki", IsTriage: true))).Content.ReadFromJsonAsync<CreatedAgentResult>();
        var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        return (new DoiQApiClient(userHttp), userHttp, agent, cookie);
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
        connection.On<CommentAddedMessage>(BoardHubEvents.CommentAdded, m => sync.ApplyAsync(m));
        connection.On<SeenChangedMessage>(BoardHubEvents.SeenChanged, m => sync.ApplyAsync(m));
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

    private async Task<TaskVm> TaskAsync(DoiQApiClient browser, int id) => (await browser.GetBoard(Ct)).Single(t => t.Id == id);

    /// <summary>What opening the drawer does: the board holds counts only, the drawer loads the task's lists.</summary>
    private async Task<TaskVm> OpenAsync(DoiQApiClient browser, int id) => await browser.LoadTask(id, Ct) ?? throw new InvalidOperationException($"Task {id} is gone.");

    private static async Task<TaskDetailDto> CreateAsync(HttpClient who, string title) =>
        (await (await who.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title))).Content.ReadFromJsonAsync<TaskDetailDto>())!;

    private static async Task PatchAsync(HttpClient who, int id, UpdateTaskRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/tasks/{id}") { Content = JsonContent.Create(body) };
        (await who.SendAsync(request)).EnsureSuccessStatusCode();
    }

    // ---- the HTTP answer to a stale write ----

    [Theory]
    [InlineData("PATCH", "")]
    [InlineData("POST", "/move")]
    [InlineData("POST", "/assign")]
    public async Task StaleWrite_OnEachVersionedRoute_Answers409WithAProblemNamingWhoChangedIt(string method, string suffix)
    {
        // Arrange
        var (_, userHttp, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(userHttp, "x");
        var stale = created.Task.Version;
        await PatchAsync(agent, created.Task.Id, new UpdateTaskRequest(Title: "changed by the agent"));
        object body = suffix switch
        {
            "/move" => new MoveTaskRequest(BoardStatus.Doing, ExpectedVersion: stale),
            "/assign" => new AssignRequest("Loki", stale),
            _ => new UpdateTaskRequest(Title: "mine", ExpectedVersion: stale),
        };

        // Act
        var response = await userHttp.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/tasks/{created.Task.Id}{suffix}") { Content = JsonContent.Create(body) });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale", problem.GetProperty("code").GetString());
        Assert.Equal("Loki", problem.GetProperty("changedBy").GetString());
        Assert.Equal(stale + 1, problem.GetProperty("currentVersion").GetInt64());
    }

    // ---- the browser's data layer ----

    [Fact]
    public async Task DoiQApiClient_MoveAfterAnAgentChangedTheTask_ThrowsAConflictNamingTheAgentAndShowsTheirVersion()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(agent, "Race me");
        await browser.GetBoard(Ct);
        await PatchAsync(agent, created.Task.Id, new UpdateTaskRequest(Title: "Agent title"));

        // Act
        var ex = await Record.ExceptionAsync(() => browser.MoveAsync(created.Task.Id, BoardLane.Doing, null));

        // Assert
        var conflict = Assert.IsType<TaskConflictException>(ex);
        Assert.Equal("Loki", conflict.ChangedBy);
        var task = await TaskAsync(browser, created.Task.Id);
        Assert.Equal("Agent title", task.Title);
        Assert.Equal(BoardLane.New, task.Lane);
    }

    [Fact]
    public async Task DoiQApiClient_MoveAfterAConflict_SucceedsWithTheReloadedVersion()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(agent, "Race me");
        await browser.GetBoard(Ct);
        await PatchAsync(agent, created.Task.Id, new UpdateTaskRequest(Title: "Agent title"));
        await Record.ExceptionAsync(() => browser.MoveAsync(created.Task.Id, BoardLane.Doing, null));

        // Act
        await browser.MoveAsync(created.Task.Id, BoardLane.Doing, null);

        // Assert
        Assert.Equal(BoardLane.Doing, (await TaskAsync(browser, created.Task.Id)).Lane);
    }

    [Fact]
    public async Task DoiQApiClient_TitleSavedFromAnOlderVersionThanHeld_IsAConflictEvenThoughTheBoardWasUpToDate()
    {
        // Arrange
        var (browser, _, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(agent, "Edit me");
        await browser.GetBoard(Ct);
        var startedFrom = (await TaskAsync(browser, created.Task.Id)).Version;
        await PatchAsync(agent, created.Task.Id, new UpdateTaskRequest(Description: "agent notes"));
        await browser.CatchUpAsync();   // the board now holds the newer version, but the user's edit began before it

        // Act
        var ex = await Record.ExceptionAsync(() => browser.SetTitleAsync(created.Task.Id, "My title", startedFrom));

        // Assert
        Assert.IsType<TaskConflictException>(ex);
        Assert.Equal("Edit me", (await TaskAsync(browser, created.Task.Id)).Title);
    }

    [Fact]
    public async Task DoiQApiClient_WriteToATaskDeletedElsewhere_ThrowsNotFoundAndDropsTheCard()
    {
        // Arrange
        var (browser, userHttp, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(agent, "Gone soon");
        await browser.GetBoard(Ct);
        (await userHttp.DeleteAsync($"/api/tasks/{created.Task.Id}")).EnsureSuccessStatusCode();

        // Act
        var ex = await Record.ExceptionAsync(() => browser.SetTitleAsync(created.Task.Id, "Too late"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<ApiException>(ex).Status);
        Assert.DoesNotContain(await browser.GetBoard(Ct), t => t.Id == created.Task.Id);
    }

    [Fact]
    public async Task DoiQApiClient_FreshLoadWithAnUnreadAgentComment_MarksOnlyThatCommentNew()
    {
        // Arrange
        var (browser, userHttp, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(userHttp, "Talk");
        (await agent.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("old news"))).EnsureSuccessStatusCode();
        (await userHttp.PostAsync($"/api/tasks/{created.Task.Id}/seen", null)).EnsureSuccessStatusCode();
        (await agent.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("fresh news"))).EnsureSuccessStatusCode();

        // Act
        var task = await OpenAsync(browser, created.Task.Id);

        // Assert
        Assert.Equal([false, true], task.Comments.Select(c => c.IsNew));
    }

    [Fact]
    public async Task DoiQApiClient_MarkSeen_ClearsTheNewMarkersOnAFreshLoadToo()
    {
        // Arrange
        var (browser, userHttp, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(userHttp, "Talk");
        (await agent.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("hello"))).EnsureSuccessStatusCode();
        await browser.GetBoard(Ct);

        // Act
        await browser.MarkSeenAsync(created.Task.Id);

        // Assert
        var reloaded = new DoiQApiClient(userHttp);
        Assert.All((await TaskAsync(reloaded, created.Task.Id)).Comments, c => Assert.False(c.IsNew));
    }

    [Fact]
    public async Task DoiQApiClient_AgentSeesTheUsersComment_ReceiptAppearsLiveWithoutARefresh()
    {
        // Arrange
        var (browser, userHttp, agent, cookie) = await ArrangeAsync();
        var created = await CreateAsync(userHttp, "Ping");
        (await userHttp.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("are you there?"))).EnsureSuccessStatusCode();
        await browser.GetBoard(Ct);
        await ConnectAsync(browser, cookie);
        var before = await OpenAsync(browser, created.Task.Id);
        Assert.Empty(before.SeenByFor(before.Comments[0]));

        // Act
        (await agent.PostAsync($"/api/tasks/{created.Task.Id}/seen", null)).EnsureSuccessStatusCode();

        // Assert
        Assert.True(await EventuallyAsync(async () => (await TaskAsync(browser, created.Task.Id)) is { } t && t.SeenByFor(t.Comments[0]).Count == 1));
        var task = await TaskAsync(browser, created.Task.Id);
        Assert.Equal(["Loki"], task.SeenByFor(task.Comments[0]));
    }

    [Fact]
    public async Task DoiQApiClient_FreshLoadAfterAnAgentSawTheComment_StillShowsTheReceipt()
    {
        // Arrange
        var (_, userHttp, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(userHttp, "Ping");
        (await userHttp.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("are you there?"))).EnsureSuccessStatusCode();
        (await agent.PostAsync($"/api/tasks/{created.Task.Id}/seen", null)).EnsureSuccessStatusCode();

        // Act
        var task = await OpenAsync(new DoiQApiClient(userHttp), created.Task.Id);

        // Assert
        Assert.Equal(["Loki"], task.SeenByFor(task.Comments[0]));
    }

    [Fact]
    public async Task DoiQApiClient_AgentSawTheTaskBeforeAFollowUpComment_ReceiptOnlyCoversTheEarlierOne()
    {
        // Arrange
        var (_, userHttp, agent, _) = await ArrangeAsync();
        var created = await CreateAsync(userHttp, "Ping");
        (await userHttp.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("first"))).EnsureSuccessStatusCode();
        (await agent.PostAsync($"/api/tasks/{created.Task.Id}/seen", null)).EnsureSuccessStatusCode();
        (await userHttp.PostAsJsonAsync($"/api/tasks/{created.Task.Id}/comments", new AddCommentRequest("second"))).EnsureSuccessStatusCode();

        // Act
        var task = await OpenAsync(new DoiQApiClient(userHttp), created.Task.Id);

        // Assert
        Assert.Equal([1, 0], task.Comments.Select(c => task.SeenByFor(c).Count));
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

public class NoticeDescribeTests
{
    [Fact]
    public void Describe_ConflictByAnAgent_NamesTheAgentAsAWarning()
    {
        // Arrange
        var ex = new TaskConflictException(7, "Hermes", "stale");

        // Act
        var (level, text) = NoticeService.Describe(ex);

        // Assert
        Assert.Equal(NoticeLevel.Warning, level);
        Assert.StartsWith("Hermes just changed this", text);
    }

    [Fact]
    public void Describe_ConflictByTheUserInAnotherTab_SaysAnotherTabNotTheServerName()
    {
        // Arrange
        var ex = new TaskConflictException(7, Fmt.HumanServerName, "stale");

        // Act
        var (_, text) = NoticeService.Describe(ex);

        // Assert
        Assert.Contains("another tab", text);
        Assert.DoesNotContain(Fmt.HumanServerName, text);
    }

    [Fact]
    public void Describe_UnreachableServer_IsAnOfflineError()
    {
        // Arrange
        var ex = new ApiException("Could not reach the server.");

        // Act
        var (level, text) = NoticeService.Describe(ex);

        // Assert
        Assert.Equal(NoticeLevel.Error, level);
        Assert.Contains("offline", text);
    }

    [Fact]
    public void Describe_Forbidden_ShowsTheServersReason()
    {
        // Arrange
        var ex = new ApiException("Agents cannot delete tasks.", HttpStatusCode.Forbidden);

        // Act
        var (level, text) = NoticeService.Describe(ex);

        // Assert
        Assert.Equal((NoticeLevel.Error, "Agents cannot delete tasks."), (level, text));
    }

    [Fact]
    public void Describe_ServerError_HidesTheDetailAndSaysNothingWasSaved()
    {
        // Arrange
        var ex = new ApiException("System.NullReferenceException at ...", HttpStatusCode.InternalServerError);

        // Act
        var (_, text) = NoticeService.Describe(ex);

        // Assert
        Assert.Contains("500", text);
        Assert.DoesNotContain("NullReference", text);
    }

    [Fact]
    public void Describe_Unauthorized_SaysNothingBecauseTheLoginPageTakesOver()
    {
        // Arrange
        var ex = new ApiException("Not signed in.", HttpStatusCode.Unauthorized);

        // Act
        var (_, text) = NoticeService.Describe(ex);

        // Assert
        Assert.Equal("", text);
    }

    [Fact]
    public async Task TryAsync_ActionThrows_ReturnsTheFailureAndShowsOneErrorNotice()
    {
        // Arrange
        var notices = new NoticeService();

        // Act
        var err = await notices.TryAsync(() => throw new InvalidOperationException("boom"));

        // Assert
        Assert.IsType<InvalidOperationException>(err);
        Assert.Equal(NoticeLevel.Error, Assert.Single(notices.Items).Level);
    }

    [Fact]
    public async Task TryAsync_ActionSucceeds_ShowsNothing()
    {
        // Arrange
        var notices = new NoticeService();

        // Act
        var err = await notices.TryAsync(() => Task.CompletedTask);

        // Assert
        Assert.Null(err);
        Assert.Empty(notices.Items);
    }

    [Fact]
    public void Show_SameErrorTwice_KeepsOneNotice()
    {
        // Arrange
        var notices = new NoticeService();
        var first = notices.Show(NoticeLevel.Error, "again");

        // Act
        notices.Show(NoticeLevel.Error, "again");

        // Assert
        Assert.Single(notices.Items);
        Assert.NotEqual(first, notices.Items[0].Id);
    }

    [Fact]
    public void ApplySeen_OlderPositionThanHeld_IsIgnored()
    {
        // Arrange
        var state = new BoardState();
        var task = new TaskVm(1, "t", "", BoardLane.New, AssigneeVm.Unassigned, null, false, false, false, "User", DateTimeOffset.Now, DateTimeOffset.Now, "User", [], [], []);
        state.Reset([task], 0);
        state.ApplySeen(1, "Hermes", 10);

        // Act
        var changed = state.ApplySeen(1, "Hermes", 4);

        // Assert
        Assert.False(changed);
        Assert.Equal(10, state.Find(1)!.SeenBy!.Single().SeenThroughEventId);
    }
}

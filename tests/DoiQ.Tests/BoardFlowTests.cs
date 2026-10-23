using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using ModelContextProtocol.Client;

namespace DoiQ.Tests;

/// <summary>Boards over the real server: the REST rules, the MCP tools and the webhook payload.</summary>
[Collection("Http")]   // the database path and timings travel through environment variables
public sealed class BoardFlowTests : IAsyncLifetime
{
    private const string Secret = "board-secret";
    private const string Route = "/webhooks/doiq";

    private FlowHost _host = null!;
    private WebhookReceiver _hermes = null!;
    private CreatedAgentResult _agent = null!;

    public async Task InitializeAsync()
    {
        _hermes = await WebhookReceiver.StartAsync(Secret);
        _host = new FlowHost(debounceSeconds: 1);
        await _host.StartAsync();
        _agent = await _host.CreateAgentAsync("Hermes", triage: true, webhookUrl: _hermes.BaseUrl + Route, secret: Secret);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _hermes.DisposeAsync();
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);

    private async Task<TaskDetailDto> CreateOnAsync(int boardId, string title, BoardStatus? status = null, string? assignee = null)
    {
        var resp = await _host.User.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title, Status: status, Assignee: assignee, BoardId: boardId));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TaskDetailDto>())!;
    }

    private async Task<int> MainIdAsync() => (await _host.User.GetFromJsonAsync<List<BoardDto>>("/api/boards"))!.Single(b => b.Name == "Main").Id;

    // ───────────────────────── REST ─────────────────────────

    [Fact]
    public async Task GetBoards_FreshServer_ListsOneBoardNamedMain()
    {
        // Arrange
        // (fresh server)

        // Act
        var boards = await _host.User.GetFromJsonAsync<List<BoardDto>>("/api/boards");

        // Assert
        Assert.Equal(["Main"], boards!.Select(b => b.Name));
    }

    [Fact]
    public async Task PostBoards_ByAnAgent_IsForbidden()
    {
        // Arrange
        var agent = _host.AgentHttp(_agent.ApiKey);

        // Act
        var response = await agent.PostAsJsonAsync("/api/boards", new CreateBoardRequest("Sneaky"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetBoards_ByAnAgent_ListsTheBoards()
    {
        // Arrange
        var agent = _host.AgentHttp(_agent.ApiKey);

        // Act
        var response = await agent.GetAsync("/api/boards");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostBoards_DuplicateNameInOtherCase_Answers409()
    {
        // Arrange
        await _host.UserAddBoardAsync("Work");

        // Act
        var response = await _host.User.PostAsJsonAsync("/api/boards", new CreateBoardRequest("WORK"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PostBoards_EmptyName_Answers400()
    {
        // Arrange
        // (nothing)

        // Act
        var response = await _host.User.PostAsJsonAsync("/api/boards", new CreateBoardRequest("  "));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutBoards_RenamesTheBoard()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");

        // Act
        var response = await _host.User.PutAsJsonAsync($"/api/boards/{work.Id}", new RenameBoardRequest("Job", work.Version));

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.Equal("Job", (await response.Content.ReadFromJsonAsync<BoardDto>())!.Name);
    }

    [Fact]
    public async Task DeleteBoards_BareDelete_DoesNothing()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");

        // Act
        var response = await _host.User.DeleteAsync($"/api/boards/{work.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, (await _host.User.GetFromJsonAsync<List<BoardDto>>("/api/boards"))!.Count);
    }

    [Fact]
    public async Task DeleteBoards_WithAnOpenTask_Answers409WithTheCount()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await CreateOnAsync(work.Id, "Open one");
        await CreateOnAsync(work.Id, "Open two", BoardStatus.Waiting);

        // Act
        var response = await _host.User.DeleteAsync($"/api/boards/{work.Id}?confirm=true");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, problem.RootElement.GetProperty("openTasks").GetInt32());
        Assert.Contains("2 tasks", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task DeleteBoards_AfterFinishingTheTasks_RemovesTheBoard()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        var task = await CreateOnAsync(work.Id, "Only one");
        await _host.User.PostAsJsonAsync($"/api/tasks/{task.Task.Id}/move", new MoveTaskRequest(BoardStatus.Done));

        // Act
        var response = await _host.User.DeleteAsync($"/api/boards/{work.Id}?confirm=true");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.User.GetAsync($"/api/tasks/{task.Task.Id}")).StatusCode);
        Assert.Equal(["Main"], (await _host.User.GetFromJsonAsync<List<BoardDto>>("/api/boards"))!.Select(b => b.Name));
    }

    [Fact]
    public async Task DeleteBoards_ByAnAgent_IsForbidden()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");

        // Act
        var response = await _host.AgentHttp(_agent.ApiKey).DeleteAsync($"/api/boards/{work.Id}?confirm=true");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTasks_SeveralBoardsAndNoBoardGiven_Answers400ListingTheBoards()
    {
        // Arrange
        await _host.UserAddBoardAsync("Work");

        // Act
        var response = await _host.User.GetAsync("/api/tasks");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Work", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetTasks_ByBoardId_ReturnsOnlyThatBoard()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await CreateOnAsync(await MainIdAsync(), "On Main");
        await CreateOnAsync(work.Id, "On Work");

        // Act
        var list = await _host.User.GetFromJsonAsync<List<TaskSummaryDto>>($"/api/tasks?boardId={work.Id}");

        // Assert
        Assert.Equal(["On Work"], list!.Select(t => t.Title));
    }

    [Fact]
    public async Task GetEvents_ByBoardId_ReturnsOnlyThatBoardsEvents()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await CreateOnAsync(await MainIdAsync(), "On Main");
        await CreateOnAsync(work.Id, "On Work");

        // Act
        var feed = await _host.User.GetFromJsonAsync<EventFeedDto>($"/api/events?after=0&boardId={work.Id}");

        // Assert
        Assert.Equal([work.Id], feed!.Events.Select(e => e.BoardId));
    }

    [Fact]
    public async Task PostMove_NeighbourOnAnotherBoard_Answers400()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        var mine = await CreateOnAsync(await MainIdAsync(), "On Main");
        var foreign = await CreateOnAsync(work.Id, "On Work");

        // Act
        var response = await _host.User.PostAsJsonAsync($"/api/tasks/{mine.Task.Id}/move", new MoveTaskRequest(BoardStatus.New, BeforeTaskId: foreign.Task.Id));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ───────────────────────── MCP ─────────────────────────

    [Fact]
    public async Task ListBoards_ByAnAgent_ReturnsIdsNamesAndCounts()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await CreateOnAsync(work.Id, "Open");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var boards = (await mcp.CallToolAsync("list_boards", Args())).As<List<BoardDto>>();

        // Assert
        var listed = boards.Single(b => b.Name == "Work");
        Assert.Equal((work.Id, 1, 0), (listed.Id, listed.OpenTasks, listed.DoneTasks));
    }

    [Fact]
    public async Task CreateTask_OnlyOneBoard_NeedsNoBoardArgumentAndReportsTheBoard()
    {
        // Arrange
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var created = (await mcp.CallToolAsync("create_task", Args(("title", "Default board")))).As<CreateTaskResult>();

        // Assert
        Assert.Equal("Main", created.Task.Task.BoardName);
        Assert.True(created.Task.Task.BoardId > 0);
    }

    [Fact]
    public async Task CreateTask_SeveralBoardsAndNoneGiven_FailsListingTheBoards()
    {
        // Arrange
        await _host.UserAddBoardAsync("Work");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var result = await mcp.CallToolAsync("create_task", Args(("title", "Which board?")));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Main", result.Text());
        Assert.Contains("Work", result.Text());
        Assert.Contains("boardName", result.Text());
    }

    [Fact]
    public async Task CreateTask_ByBoardName_LandsOnThatBoard()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var created = (await mcp.CallToolAsync("create_task", Args(("title", "Quarterly report"), ("boardName", "work")))).As<CreateTaskResult>();

        // Assert
        Assert.Equal(work.Id, created.Task.Task.BoardId);
    }

    [Fact]
    public async Task SearchTasks_ByBoardId_FindsOnlyThatBoardsTasks()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await CreateOnAsync(await MainIdAsync(), "shared word on Main");
        await CreateOnAsync(work.Id, "shared word on Work");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var hits = (await mcp.CallToolAsync("search_tasks", Args(("q", "shared word"), ("boardId", work.Id)))).As<TaskPageDto>().Tasks;

        // Assert
        Assert.Equal(["shared word on Work"], hits.Select(t => t.Title));
        Assert.Equal("Work", hits.Single().BoardName);
    }

    [Fact]
    public async Task SearchTasks_SeveralBoardsAndNoneGiven_FailsListingTheBoards()
    {
        // Arrange
        await _host.UserAddBoardAsync("Work");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var result = await mcp.CallToolAsync("search_tasks", Args(("q", "anything")));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Work", result.Text());
    }

    [Fact]
    public async Task GetInbox_TriageAgentWithNewTasksOnTwoBoards_SeesBothWithTheirBoards()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        await CreateOnAsync(await MainIdAsync(), "New on Main");
        await CreateOnAsync(work.Id, "New on Work");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var inbox = (await mcp.CallToolAsync("get_inbox", Args())).As<List<InboxItemDto>>();

        // Assert
        Assert.Equal([("Main", "Triage"), ("Work", "Triage")], inbox.OrderBy(i => i.Task.BoardId).Select(i => (i.Task.BoardName, i.Reason)));
    }

    [Fact]
    public async Task MoveTask_NeighbourOnAnotherBoard_FailsWithAdvice()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");
        var mine = await CreateOnAsync(await MainIdAsync(), "Mine", assignee: "Hermes");
        var foreign = await CreateOnAsync(work.Id, "Theirs");
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var result = await mcp.CallToolAsync("move_task", Args(("taskId", mine.Task.Id), ("status", "Doing"), ("beforeTaskId", foreign.Task.Id)));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("another board", result.Text());
    }

    [Fact]
    public async Task ListTools_Always_OffersNoWayToChangeBoards()
    {
        // Arrange
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        var names = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();

        // Assert
        Assert.Contains("list_boards", names);
        Assert.DoesNotContain(names, n => n.Contains("board") && n != "list_boards");
        Assert.DoesNotContain(names, n => n.Contains("transfer") || n.Contains("copy"));
    }

    // ───────────────────────── webhook ─────────────────────────

    [Fact]
    public async Task OutboxWorker_TaskOnASecondBoard_PostsTheBoardIdAndName()
    {
        // Arrange
        var work = await _host.UserAddBoardAsync("Work");

        // Act
        var task = await CreateOnAsync(work.Id, "Write the report", assignee: "Hermes");

        // Assert
        var call = Assert.Single(await _hermes.WaitForAsync(Route));
        using var payload = JsonDocument.Parse(call.Body);
        Assert.Equal(task.Task.Id, payload.RootElement.GetProperty("taskId").GetInt32());
        Assert.Equal(work.Id, payload.RootElement.GetProperty("boardId").GetInt32());
        Assert.Equal("Work", payload.RootElement.GetProperty("boardName").GetString());
    }
}

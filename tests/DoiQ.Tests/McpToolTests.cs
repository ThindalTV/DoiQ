using System.Net.Http.Json;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using ModelContextProtocol.Client;

namespace DoiQ.Tests;

/// <summary>/mcp over the real server: tools, actor from the API key, guardrail errors, claims, read markers, idempotency.</summary>
[Collection("Http")]
public sealed class McpToolTests : IClassFixture<McpToolTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public FlowHost Host { get; } = new(debounceSeconds: 30);
        public McpClient Hermes { get; private set; } = null!;
        public McpClient Odin { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await Host.StartAsync();
            var hermes = await Host.CreateAgentAsync("Hermes", triage: true);
            var odin = await Host.CreateAgentAsync("Odin");
            Hermes = await Host.McpAsync(hermes.ApiKey);
            Odin = await Host.McpAsync(odin.ApiKey);
        }

        public async Task DisposeAsync()
        {
            await Hermes.DisposeAsync();
            await Odin.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private readonly Fixture _f;

    public McpToolTests(Fixture fixture) => _f = fixture;

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);

    private static async Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(McpClient c, string tool, params (string Key, object? Value)[] args) =>
        await c.CallToolAsync(tool, Args(args));

    [Fact]
    public async Task ListTools_Always_ExposesTheNineteenToolsWithInstructions()
    {
        // Arrange
        string[] expected =
        [
            "list_boards", "get_inbox", "get_changes", "search_tasks", "get_task", "create_task", "update_task", "move_task", "assign_task", "ask_user", "upsert_field",
            "add_comment", "mark_seen", "mark_handled", "add_subtask", "update_subtask", "claim_task", "renew_claim", "release_task",
        ];

        // Act
        var tools = await _f.Hermes.ListToolsAsync();

        // Assert
        Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.Description!.Length > 40, $"{t.Name} needs a description that instructs the agent"));
        Assert.Contains("assigned to you", tools.Single(t => t.Name == "move_task").Description);
        Assert.Contains("comment", tools.Single(t => t.Name == "move_task").Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTask_ByEachAgent_RecordsThatAgentAsTheActor()
    {
        // Arrange
        var title = "who am i " + Guid.NewGuid().ToString("N");

        // Act
        var created = (await CallAsync(_f.Odin, "create_task", ("title", title))).As<CreateTaskResult>();

        // Assert
        Assert.Equal("Odin", created.Task.CreatedBy);
        Assert.Equal("Odin", created.Task.Task.LastActivityBy);
    }

    [Fact]
    public async Task MoveTask_AgentOnTheUsersTask_MovesItForTheUser()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("mine " + Guid.NewGuid().ToString("N"), assignee: "User", status: BoardStatus.Doing);

        // Act
        var result = await CallAsync(_f.Hermes, "move_task", ("taskId", task.Task.Id), ("status", "Done"));

        // Assert
        Assert.NotEqual(true, result.IsError);
        var after = await _f.Host.UserGetAsync(task.Task.Id);
        Assert.Equal(BoardStatus.Done, after.Task.Status);
        Assert.Equal("User", after.Task.Assignee);
    }

    [Fact]
    public async Task MoveTask_AgentFromWaitingToDone_FailsWithAdviceToGoThroughDoing()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("skip " + Guid.NewGuid().ToString("N"), assignee: "Hermes");

        // Act
        var result = await CallAsync(_f.Hermes, "move_task", ("taskId", task.Task.Id), ("status", "Done"));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Move it to Doing", result.Text());
    }

    [Fact]
    public async Task AskUser_AgentNeedsInformation_HandsTheTaskBackWithTheQuestion()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("ask " + Guid.NewGuid().ToString("N"), assignee: "Hermes");

        // Act
        var result = await CallAsync(_f.Hermes, "ask_user", ("taskId", task.Task.Id), ("question", "Which date?"));

        // Assert
        Assert.NotEqual(true, result.IsError);
        var after = await _f.Host.UserGetAsync(task.Task.Id);
        Assert.Equal(BoardStatus.Waiting, after.Task.Status);
        Assert.Equal("User", after.Task.Assignee);
        Assert.Equal("Which date?", Assert.Single(after.Comments).Body);
    }

    [Fact]
    public async Task MoveTask_AgentOnAnotherAgentsTask_FailsWithAdviceToComment()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("odin's " + Guid.NewGuid().ToString("N"), assignee: "Odin", status: BoardStatus.Waiting);

        // Act
        var result = await CallAsync(_f.Hermes, "move_task", ("taskId", task.Task.Id), ("status", "Doing"));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("You can only comment on it", result.Text());
    }

    [Fact]
    public async Task MoveTask_UnknownStatus_FailsNamingTheValidOnes()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("status " + Guid.NewGuid().ToString("N"), assignee: "Hermes");

        // Act
        var result = await CallAsync(_f.Hermes, "move_task", ("taskId", task.Task.Id), ("status", "Finished"));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("New, Waiting, Doing or Done", result.Text());
    }

    [Fact]
    public async Task GetTask_MissingTask_FailsWithNotFoundText()
    {
        // Arrange / Act
        var result = await CallAsync(_f.Hermes, "get_task", ("taskId", 987654));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("987654", result.Text());
    }

    [Fact]
    public async Task ClaimTask_TwoAgentsRaceForOnePoolTask_ExactlyOneWins()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("pool " + Guid.NewGuid().ToString("N"), assignee: "any");

        // Act
        var results = await Task.WhenAll(
            CallAsync(_f.Hermes, "claim_task", ("taskId", task.Task.Id)),
            CallAsync(_f.Odin, "claim_task", ("taskId", task.Task.Id)));

        // Assert
        Assert.Single(results, r => r.IsError != true);
        var loser = Assert.Single(results, r => r.IsError == true);
        Assert.Contains("already claimed", loser.Text());
        var board = await _f.Host.UserGetAsync(task.Task.Id);
        Assert.NotNull(board.Task.ClaimedBy);
    }

    [Fact]
    public async Task RenewClaim_ByAnAgentThatDoesNotHoldIt_FailsAndReleaseLetsTheOtherAgentClaim()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("lease " + Guid.NewGuid().ToString("N"), assignee: "any");
        await CallAsync(_f.Hermes, "claim_task", ("taskId", task.Task.Id));

        // Act
        var renewByOther = await CallAsync(_f.Odin, "renew_claim", ("taskId", task.Task.Id));
        await CallAsync(_f.Hermes, "release_task", ("taskId", task.Task.Id));
        var claimAfterRelease = await CallAsync(_f.Odin, "claim_task", ("taskId", task.Task.Id));

        // Assert
        Assert.True(renewByOther.IsError);
        Assert.Contains("do not hold the claim", renewByOther.Text());
        Assert.NotEqual(true, claimAfterRelease.IsError);
        Assert.Equal("Odin", claimAfterRelease.As<TaskSummaryDto>().ClaimedBy);
    }

    [Fact]
    public async Task GetInbox_MarkSeenOnly_KeepsTheTaskUntilItIsMarkedHandled()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("inbox " + Guid.NewGuid().ToString("N"), assignee: "Hermes");
        var id = task.Task.Id;
        var latest = (await CallAsync(_f.Hermes, "get_task", ("taskId", id))).As<TaskDetailDto>().LatestEventId;

        // Act
        await CallAsync(_f.Hermes, "mark_seen", ("taskId", id), ("throughEventId", latest));
        var afterSeen = (await CallAsync(_f.Hermes, "get_inbox")).As<List<InboxItemDto>>();
        await CallAsync(_f.Hermes, "mark_handled", ("taskId", id), ("throughEventId", latest));
        var afterHandled = (await CallAsync(_f.Hermes, "get_inbox")).As<List<InboxItemDto>>();

        // Assert
        Assert.Contains(afterSeen, i => i.Task.Id == id);
        Assert.DoesNotContain(afterHandled, i => i.Task.Id == id);
    }

    [Fact]
    public async Task GetInbox_UserCommentsAfterTheAgentHandledIt_ReturnsTheTaskAgain()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("again " + Guid.NewGuid().ToString("N"), assignee: "Hermes");
        var id = task.Task.Id;
        await CallAsync(_f.Hermes, "mark_handled", ("taskId", id));

        // Act
        await _f.Host.User.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("one more thing"));
        var inbox = (await CallAsync(_f.Hermes, "get_inbox")).As<List<InboxItemDto>>();

        // Assert
        Assert.Contains(inbox, i => i.Task.Id == id);
    }

    [Fact]
    public async Task GetChanges_AfterTheUsersEdit_ReturnsItWithoutTheAgentsOwnEventsAndACursor()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("changes " + Guid.NewGuid().ToString("N"), assignee: "Hermes");
        var id = task.Task.Id;
        var before = (await CallAsync(_f.Hermes, "get_changes", ("after", 0), ("limit", 1000))).As<EventFeedDto>().NextCursor;
        await CallAsync(_f.Hermes, "add_comment", ("taskId", id), ("body", "mine"));
        await _f.Host.User.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("yours"));

        // Act
        var feed = (await CallAsync(_f.Hermes, "get_changes", ("after", before))).As<EventFeedDto>();

        // Assert
        var ev = Assert.Single(feed.Events);
        Assert.Equal("User", ev.Actor);
        Assert.Equal(EventType.CommentAdded, ev.Type);
        Assert.Equal(ev.Id, feed.NextCursor);
    }

    [Fact]
    public async Task AddComment_SameRequestKeyTwice_StoresOneComment()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("retry " + Guid.NewGuid().ToString("N"), assignee: "Hermes");
        var args = new (string, object?)[] { ("taskId", task.Task.Id), ("body", "hello"), ("requestKey", "k-1") };
        var first = (await CallAsync(_f.Hermes, "add_comment", args)).As<CommentDto>();

        // Act
        var second = (await CallAsync(_f.Hermes, "add_comment", args)).As<CommentDto>();

        // Assert
        Assert.Equal(first.Id, second.Id);
        Assert.Single((await _f.Host.UserGetAsync(task.Task.Id)).Comments);
    }

    [Fact]
    public async Task CreateTask_SameRequestKeyTwice_ReturnsTheSameTask()
    {
        // Arrange
        var args = new (string, object?)[] { ("title", "keyed " + Guid.NewGuid().ToString("N")), ("requestKey", "create-1") };
        var first = (await CallAsync(_f.Hermes, "create_task", args)).As<CreateTaskResult>();

        // Act
        var second = (await CallAsync(_f.Hermes, "create_task", args)).As<CreateTaskResult>();

        // Assert
        Assert.True(first.Created);
        Assert.Equal(first.Task.Task.Id, second.Task.Task.Id);
    }

    [Fact]
    public async Task SearchTasks_MoreMatchesThanTheLimit_ReturnsAPageAndANextCursorThatContinues()
    {
        // Arrange
        var word = "paged" + Guid.NewGuid().ToString("N");
        foreach (var n in new[] { "a", "b", "c" }) await _f.Host.UserCreateAsync($"{word} {n}");
        var first = (await CallAsync(_f.Hermes, "search_tasks", ("q", word), ("sort", "title"), ("limit", 2))).As<TaskPageDto>();

        // Act
        var second = (await CallAsync(_f.Hermes, "search_tasks", ("q", word), ("sort", "title"), ("limit", 2), ("cursor", first.NextCursor))).As<TaskPageDto>();

        // Assert
        Assert.Equal([$"{word} a", $"{word} b"], first.Tasks.Select(t => t.Title));
        Assert.Equal([$"{word} c"], second.Tasks.Select(t => t.Title));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task UpsertField_SameLabelInOtherCaseTwice_UpdatesOneField()
    {
        // Arrange
        var task = await _f.Host.UserCreateAsync("fields " + Guid.NewGuid().ToString("N"), assignee: "Hermes");
        var id = task.Task.Id;
        await CallAsync(_f.Hermes, "upsert_field", ("taskId", id), ("label", "Research"), ("value", "first"));

        // Act
        await CallAsync(_f.Hermes, "upsert_field", ("taskId", id), ("label", "research"), ("value", "second"));

        // Assert
        var field = Assert.Single((await _f.Host.UserGetAsync(id)).Fields);
        Assert.Equal("second", field.Value);
    }
}

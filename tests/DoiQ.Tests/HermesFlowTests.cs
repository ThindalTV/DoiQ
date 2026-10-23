using System.Net.Http.Json;
using System.Text.Json;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;

namespace DoiQ.Tests;

/// <summary>
/// The whole loop against the real server and a throwaway database: user action -> outbox -> signed webhook to a
/// receiver that stands in for Hermes -> agent works over MCP with its key -> change reaches the board -> notify webhook.
/// </summary>
[Collection("Http")]   // the database path and timings travel through environment variables
public sealed class HermesFlowTests : IAsyncLifetime
{
    private const string Secret = "flow-secret";
    private const string Route = "/webhooks/doiq";
    private const string NotifyRoute = "/webhooks/doiq-notify";

    private FlowHost _host = null!;
    private WebhookReceiver _hermes = null!;
    private CreatedAgentResult _agent = null!;

    public async Task InitializeAsync()
    {
        _hermes = await WebhookReceiver.StartAsync(Secret);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        await _hermes.DisposeAsync();
    }

    private async Task BootAsync(double debounceSeconds = 1)
    {
        _host = new FlowHost(debounceSeconds);
        await _host.StartAsync();
        _agent = await _host.CreateAgentAsync("Hermes", triage: true, webhookUrl: _hermes.BaseUrl + Route, secret: Secret, notifyUrl: _hermes.BaseUrl + NotifyRoute);
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);

    /// <summary>What Hermes does after a wake-up: read its inbox and the task, enrich, comment, start and finish the work.</summary>
    private static async Task WorkTheTaskAsync(McpClient mcp, int taskId)
    {
        await mcp.CallToolAsync("get_inbox", Args());
        await mcp.CallToolAsync("get_task", Args(("taskId", taskId)));
        await mcp.CallToolAsync("upsert_field", Args(("taskId", taskId), ("label", "Research"), ("value", "Found **three** options.")));
        await mcp.CallToolAsync("add_comment", Args(("taskId", taskId), ("body", "Looked into it, see Research.")));
        await mcp.CallToolAsync("move_task", Args(("taskId", taskId), ("status", "Doing")));
    }

    [Fact]
    public async Task OutboxWorker_UserAssignsTaskToAgent_PostsTheSignedPayloadAfterTheDebounce()
    {
        // Arrange
        await BootAsync();

        // Act
        var task = await _host.UserCreateAsync("Plan the move", assignee: "Hermes");

        // Assert
        var call = Assert.Single(await _hermes.WaitForAsync(Route));
        Assert.True(call.SignatureValid, "X-Webhook-Signature-V2 must be the hex HMAC-SHA256 of '<timestamp>.<raw body>'");
        Assert.True(long.TryParse(call.Timestamp, out var ts) && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) < 60);
        using var payload = JsonDocument.Parse(call.Body);
        var root = payload.RootElement;
        Assert.Equal(task.Task.Id, root.GetProperty("taskId").GetInt32());
        Assert.Equal("Created", root.GetProperty("type").GetString());
        Assert.Equal("User", root.GetProperty("actor").GetString());
        Assert.Equal("Plan the move", root.GetProperty("title").GetString());
        Assert.True(root.GetProperty("eventId").GetInt64() > 0);
        Assert.True(root.TryGetProperty("at", out _));
    }

    [Fact]
    public async Task McpTools_AgentWorksTheAssignedTask_ReachesTheBoard()
    {
        // Arrange
        await BootAsync();
        var task = await _host.UserCreateAsync("Plan the move", assignee: "Hermes");
        await _hermes.WaitForAsync(Route);
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        await WorkTheTaskAsync(mcp, task.Task.Id);

        // Assert
        var board = await _host.UserGetAsync(task.Task.Id);
        Assert.Equal(BoardStatus.Doing, board.Task.Status);
        var field = Assert.Single(board.Fields);
        Assert.Equal(("Research", "Found **three** options.", "Hermes"), (field.Label, field.Value, field.Author));
        var comment = Assert.Single(board.Comments);
        Assert.Equal("Hermes", comment.Author);
    }

    [Fact]
    public async Task McpTools_AgentAssignsTheTaskToTheUser_FiresTheNotifyWebhook()
    {
        // Arrange
        await BootAsync();
        var task = await _host.UserCreateAsync("Plan the move", assignee: "Hermes");
        await _hermes.WaitForAsync(Route);
        await using var mcp = await _host.McpAsync(_agent.ApiKey);
        await WorkTheTaskAsync(mcp, task.Task.Id);

        // Act
        await mcp.CallToolAsync("assign_task", Args(("taskId", task.Task.Id), ("assignee", "User")));

        // Assert
        var call = Assert.Single(await _hermes.WaitForAsync(NotifyRoute));
        Assert.True(call.SignatureValid);
        using var payload = JsonDocument.Parse(call.Body);
        Assert.Equal("Hermes assigned you: Plan the move", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OutboxWorker_AgentOnlyChangesTheBoard_NeverWakesItself()
    {
        // Arrange
        await BootAsync();
        var task = await _host.UserCreateAsync("Plan the move", assignee: "Hermes");
        await _hermes.WaitForAsync(Route);
        await using var mcp = await _host.McpAsync(_agent.ApiKey);

        // Act
        await WorkTheTaskAsync(mcp, task.Task.Id);
        await Task.Delay(TimeSpan.FromSeconds(3));   // three debounce windows

        // Assert
        Assert.Single(_hermes.CallsTo(Route));   // still only the user's create
        Assert.Empty(_hermes.CallsTo(NotifyRoute));
    }

    [Fact]
    public async Task OutboxWorker_SeveralUserChangesInsideTheWindow_SendOneWebhook()
    {
        // Arrange
        await BootAsync(debounceSeconds: 2);
        var task = await _host.UserCreateAsync("Plan the move", assignee: "Hermes");

        // Act
        await _host.User.PostAsJsonAsync($"/api/tasks/{task.Task.Id}/comments", new AddCommentRequest("one more thing"));

        // Assert
        var call = Assert.Single(await _hermes.WaitForAsync(Route));
        Assert.Contains("\"type\":\"CommentAdded\"", call.Body);   // the latest event wins
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(_hermes.CallsTo(Route));
    }

    [Fact]
    public async Task OutboxWorker_ReceiverFailsThenRecovers_RetriesUntilDeliveredOnce()
    {
        // Arrange
        await BootAsync();
        _hermes.StatusCode = 500;
        await _host.UserCreateAsync("Plan the move", assignee: "Hermes");
        await _hermes.WaitForAsync(Route, count: 2);   // at least one retry while down

        // Act
        _hermes.StatusCode = 200;
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Assert
        var calls = _hermes.CallsTo(Route);
        Assert.Equal(1, calls.Count(c => c.Status == 200));
        Assert.Equal(200, calls[^1].Status);
        var afterDelivery = calls.Count;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(afterDelivery, _hermes.CallsTo(Route).Count);   // delivered rows are not sent again
    }

    [Fact]
    public async Task OutboxWorker_ServerRestartsInsideTheDebounceWindow_StillDeliversTheQueuedRow()
    {
        // Arrange
        await BootAsync(debounceSeconds: 4);
        await _host.UserCreateAsync("Plan the move", assignee: "Hermes");
        await _host.StopAsync();
        Assert.Equal(1, PendingOutboxRows(_host.DbPath));   // Assume: the row is in the database, not only in memory
        Assert.Empty(_hermes.CallsTo(Route));

        // Act
        await _host.StartAsync();

        // Assert
        var call = Assert.Single(await _hermes.WaitForAsync(Route));
        Assert.True(call.SignatureValid);
        Assert.Contains("Plan the move", call.Body);
    }

    private static long PendingOutboxRows(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Outbox WHERE SentAt IS NULL";
        return (long)cmd.ExecuteScalar()!;
    }
}

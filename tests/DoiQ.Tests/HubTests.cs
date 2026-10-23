using System.Net;
using System.Net.Http.Json;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace DoiQ.Tests;

/// <summary>The SignalR hub over a real (in-memory) server: who may connect, and what each write pushes.</summary>
[Collection("Http")]
public sealed class HubTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HubConnection> _connections = [];

    public HubTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
    }

    private async Task<(HttpClient Client, string Cookie)> SignedInUserAsync()
    {
        var user = _factory.CreateClient();
        var response = await user.PostAsJsonAsync("/api/setup", new SetupRequest(Password));
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth=")).Split(';')[0];
        return (user, cookie);
    }

    private async Task<HttpClient> AgentClientAsync(HttpClient user)
    {
        var created = await (await user.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Loki", IsTriage: true))).Content.ReadFromJsonAsync<CreatedAgentResult>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        return client;
    }

    private HubConnection Connect(string? cookie)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubEvents.HubPath), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                if (cookie is not null) o.Headers["Cookie"] = cookie;
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private static async Task<T> WaitForAsync<T>(List<T> received, int count = 1)
    {
        for (var i = 0; i < 200 && received.Count < count; i++) await Task.Delay(25);
        Assert.True(received.Count >= count, $"Expected {count} hub message(s), got {received.Count}.");
        return received[count - 1];
    }

    [Fact]
    public async Task Negotiate_WithoutCredentials_ReturnsUnauthorized()
    {
        // Arrange
        var anonymous = _factory.CreateClient();

        // Act
        var response = await anonymous.PostAsync($"{BoardHubEvents.HubPath}/negotiate?negotiateVersion=1", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithTheUsersCookie_ReturnsOk()
    {
        // Arrange
        var (user, _) = await SignedInUserAsync();

        // Act
        var response = await user.PostAsync($"{BoardHubEvents.HubPath}/negotiate?negotiateVersion=1", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithAnAgentApiKey_IsRejected()
    {
        // Arrange
        var (user, _) = await SignedInUserAsync();
        var agent = await AgentClientAsync(user);

        // Act
        var response = await agent.PostAsync($"{BoardHubEvents.HubPath}/negotiate?negotiateVersion=1", null);

        // Assert
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task HubConnection_WithoutCredentials_FailsToStart()
    {
        // Arrange
        await SignedInUserAsync();
        var connection = Connect(cookie: null);

        // Act
        var start = connection.StartAsync();

        // Assert
        await Assert.ThrowsAnyAsync<Exception>(() => start);
    }

    [Fact]
    public async Task TaskWrites_AfterTheyCommit_PushOneMessagePerEventWithItsEventId()
    {
        // Arrange
        var (user, cookie) = await SignedInUserAsync();
        var agent = await AgentClientAsync(user);
        var changed = new List<TaskChangedMessage>();
        var comments = new List<CommentAddedMessage>();
        var fields = new List<FieldChangedMessage>();
        var deleted = new List<TaskDeletedMessage>();
        var connection = Connect(cookie);
        connection.On<TaskChangedMessage>(BoardHubEvents.TaskChanged, changed.Add);
        connection.On<CommentAddedMessage>(BoardHubEvents.CommentAdded, comments.Add);
        connection.On<FieldChangedMessage>(BoardHubEvents.FieldChanged, fields.Add);
        connection.On<TaskDeletedMessage>(BoardHubEvents.TaskDeleted, deleted.Add);
        await connection.StartAsync();

        // Act
        var created = await (await agent.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("From the agent"))).Content.ReadFromJsonAsync<TaskDetailDto>();
        var id = created!.Task.Id;
        (await agent.PostAsJsonAsync($"/api/tasks/{id}/comments", new AddCommentRequest("hello"))).EnsureSuccessStatusCode();
        (await agent.PutAsJsonAsync($"/api/tasks/{id}/fields", new FieldRequest("Notes", "first"))).EnsureSuccessStatusCode();
        (await user.DeleteAsync($"/api/tasks/{id}")).EnsureSuccessStatusCode();

        // Assert
        var createdMessage = await WaitForAsync(changed);
        var commentMessage = await WaitForAsync(comments);
        var fieldMessage = await WaitForAsync(fields);
        var deletedMessage = await WaitForAsync(deleted);
        Assert.Equal(id, createdMessage.Task.Id);
        Assert.Equal("hello", commentMessage.Comment.Body);
        Assert.Equal("Notes", fieldMessage.Field!.Label);
        Assert.Equal(id, deletedMessage.TaskId);

        var ids = new[] { createdMessage.EventId, commentMessage.EventId, fieldMessage.EventId, deletedMessage.EventId };
        Assert.Equal(ids.OrderBy(x => x), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, x => Assert.True(x > 0));

        var feed = await user.GetFromJsonAsync<EventFeedDto>("/api/events?after=0");
        Assert.Equal(ids, feed!.Events.Select(e => e.Id).Where(ids.Contains));
    }

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch { /* best effort */ } }
    }
}

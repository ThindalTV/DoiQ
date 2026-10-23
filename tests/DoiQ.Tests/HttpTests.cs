using System.Net;
using System.Net.Http.Json;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DoiQ.Tests;

/// <summary>End-to-end over HTTP against a fresh temp database: first-run setup, user-only admin routes.</summary>
[Collection("Http")]   // the database path travels through an environment variable, so these classes must not overlap
public sealed class HttpTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public HttpTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
    }

    /// <summary>A client holding the user's auth cookie after first-run setup.</summary>
    private async Task<HttpClient> SignedInUserAsync()
    {
        var user = _factory.CreateClient();
        (await user.PostAsJsonAsync("/api/setup", new SetupRequest(Password))).EnsureSuccessStatusCode();
        return user;
    }

    private async Task<(CreatedAgentResult Agent, HttpClient Client)> AgentAsync(HttpClient user)
    {
        var created = await (await user.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Loki", IsTriage: true))).Content.ReadFromJsonAsync<CreatedAgentResult>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        return (created, client);
    }

    [Fact]
    public async Task GetSetupStatus_FreshDatabase_ReportsNeedsSetup()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var status = await client.GetFromJsonAsync<SetupStatusDto>("/api/setup/status");

        // Assert
        Assert.True(status!.NeedsSetup);
    }

    [Fact]
    public async Task GetAgents_WithoutCredentials_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/agents");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_FirstCall_SignsTheUserIn()
    {
        // Arrange
        var user = _factory.CreateClient();   // keeps the auth cookie

        // Act
        var setup = await user.PostAsJsonAsync("/api/setup", new SetupRequest(Password));

        // Assert
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task PostSetup_SecondCall_ReturnsConflict()
    {
        // Arrange
        await SignedInUserAsync();

        // Act
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/setup", new SetupRequest("another one!"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetInbox_WithAgentKey_ReturnsOk()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (_, agent) = await AgentAsync(user);

        // Act
        var response = await agent.GetAsync("/api/inbox");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // the key works for the normal API
    }

    [Fact]
    public async Task GetAgents_WithAgentKey_ReturnsForbidden()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (_, agent) = await AgentAsync(user);

        // Act
        var response = await agent.GetAsync("/api/agents");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostAgents_WithAgentKey_ReturnsForbidden()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (_, agent) = await AgentAsync(user);

        // Act
        var response = await agent.PostAsJsonAsync("/api/agents", new CreateAgentRequest("Evil"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostRotateKey_WithAgentKey_ReturnsForbidden()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (created, agent) = await AgentAsync(user);

        // Act
        var response = await agent.PostAsync($"/api/agents/{created.Agent.Id}/rotate-key", null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostChangePassword_WithAgentKey_ReturnsForbidden()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (_, agent) = await AgentAsync(user);

        // Act
        var response = await agent.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(Password, "hijacked pw"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetInbox_WithKeyReplacedByRotation_ReturnsUnauthorized()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (created, agent) = await AgentAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/inbox")).StatusCode);   // Assume: the old key works first
        await user.PostAsync($"/api/agents/{created.Agent.Id}/rotate-key", null);

        // Act
        var response = await agent.GetAsync("/api/inbox");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetInbox_WithRotatedKey_ReturnsOk()
    {
        // Arrange
        var user = await SignedInUserAsync();
        var (created, _) = await AgentAsync(user);
        var rotated = await (await user.PostAsync($"/api/agents/{created.Agent.Id}/rotate-key", null)).Content.ReadFromJsonAsync<RotatedKeyResult>();
        var fresh = _factory.CreateClient();
        fresh.DefaultRequestHeaders.Add("X-Api-Key", rotated!.ApiKey);

        // Act
        var response = await fresh.GetAsync("/api/inbox");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostLogin_WrongPassword_ReturnsUnauthorized()
    {
        // Arrange
        await SignedInUserAsync();

        // Act
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("nope"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostLogin_CorrectPassword_ReturnsOk()
    {
        // Arrange
        await SignedInUserAsync();

        // Act
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(Password));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch { } }
    }
}

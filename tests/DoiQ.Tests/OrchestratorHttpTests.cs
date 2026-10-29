using System.Net;
using System.Net.Http.Json;
using DoiQ.Shared.Dtos;
using ModelContextProtocol.Client;

namespace DoiQ.Tests;

/// <summary>An orchestrator agent managing agents over /mcp and REST, and the user deleting agents.</summary>
[Collection("Http")]
public sealed class OrchestratorHttpTests : IClassFixture<OrchestratorHttpTests.Fixture>, IDisposable
{
    public sealed class Fixture : IAsyncLifetime
    {
        public FlowHost Host { get; } = new(debounceSeconds: 30);
        public CreatedAgentResult Odin { get; private set; } = null!;
        public CreatedAgentResult Hermes { get; private set; } = null!;
        public McpClient OdinMcp { get; private set; } = null!;
        public McpClient HermesMcp { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await Host.StartAsync();
            Odin = await Host.CreateAgentAsync("Odin");
            Hermes = await Host.CreateAgentAsync("Hermes", triage: true);
            (await Host.User.PutAsJsonAsync($"/api/agents/{Odin.Agent.Id}", new UpdateAgentRequest("Odin", IsOrchestrator: true))).EnsureSuccessStatusCode();
            OdinMcp = await Host.McpAsync(Odin.ApiKey);
            HermesMcp = await Host.McpAsync(Hermes.ApiKey);
        }

        public async Task DisposeAsync()
        {
            await OdinMcp.DisposeAsync();
            await HermesMcp.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private readonly Fixture _f;

    public OrchestratorHttpTests(Fixture fixture) => _f = fixture;

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    /// <summary>Cancels after two minutes so a hung call cannot block the run.</summary>
    private CancellationToken Ct => _cts.Token;

    public void Dispose() => _cts.Dispose();

    private static string Unique(string name) => name + Guid.NewGuid().ToString("N")[..8];

    private static async Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(McpClient c, string tool, params (string Key, object? Value)[] args) =>
        await c.CallToolAsync(tool, args.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public async Task RegisterAgent_ByOrchestrator_ReturnsAKeyTheNewAgentCanUse()
    {
        // Arrange
        var name = Unique("Loki");

        // Act
        var r = (await CallAsync(_f.OdinMcp, "register_agent", ("name", name), ("webhookUrl", "http://loki/hook"), ("responsibilities", "Reads the mail."))).As<CreatedAgentResult>();

        // Assert
        using var loki = _f.Host.AgentHttp(r.ApiKey);
        var me = await loki.GetFromJsonAsync<ActorDto>("/api/me", Ct);
        Assert.Equal(name, me!.Name);
        var actors = await loki.GetFromJsonAsync<List<ActorDto>>("/api/actors", Ct);
        Assert.Equal("Reads the mail.", actors!.Single(a => a.Name == name).Responsibilities);
    }

    [Fact]
    public async Task RegisterAgent_ByAgentThatIsNotAnOrchestrator_FailsWithAdvice()
    {
        // Act
        var result = await CallAsync(_f.HermesMcp, "register_agent", ("name", Unique("Loki")));

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("orchestrator", result.Text());
    }

    [Fact]
    public async Task DeleteAgent_ByOrchestrator_StopsTheAgentsKey()
    {
        // Arrange
        var name = Unique("Loki");
        var r = (await CallAsync(_f.OdinMcp, "register_agent", ("name", name))).As<CreatedAgentResult>();

        // Act
        var deleted = (await CallAsync(_f.OdinMcp, "delete_agent", ("agent", name))).As<DeletedAgentResult>();

        // Assert
        Assert.False(deleted.KeptForHistory);
        using var loki = _f.Host.AgentHttp(r.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await loki.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task ConfigureAgent_OverRest_ChangesTheWebhook()
    {
        // Arrange
        var name = Unique("Loki");
        using var odin = _f.Host.AgentHttp(_f.Odin.ApiKey);
        (await odin.PostAsJsonAsync("/api/orchestrator/agents", new RegisterAgentRequest(name), Ct)).EnsureSuccessStatusCode();

        // Act
        var resp = await odin.PatchAsJsonAsync($"/api/orchestrator/agents/{name}", new ConfigureAgentRequest(WebhookUrl: "http://new/hook"), Ct);

        // Assert
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<ConfiguredAgentResult>(Ct);
        Assert.Equal("http://new/hook", r!.Agent.WebhookUrl);
    }

    [Fact]
    public async Task ConfigureAgent_OverRestByAgentThatIsNotAnOrchestrator_Returns403()
    {
        // Arrange
        using var hermes = _f.Host.AgentHttp(_f.Hermes.ApiKey);

        // Act
        var resp = await hermes.PatchAsJsonAsync("/api/orchestrator/agents/Odin", new ConfigureAgentRequest(WebhookUrl: "http://evil/hook"), Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteAgents_ByOrchestratorOverTheUsersEndpoint_Returns403()
    {
        // Arrange
        using var odin = _f.Host.AgentHttp(_f.Odin.ApiKey);

        // Act
        var resp = await odin.DeleteAsync($"/api/agents/{_f.Hermes.Agent.Id}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteAgents_ByUser_TakesItOutOfTheList()
    {
        // Arrange
        var loki = await _f.Host.CreateAgentAsync(Unique("Loki"));

        // Act
        var resp = await _f.Host.User.DeleteAsync($"/api/agents/{loki.Agent.Id}", Ct);

        // Assert
        resp.EnsureSuccessStatusCode();
        var agents = await _f.Host.User.GetFromJsonAsync<List<AgentAdminDto>>("/api/agents", Ct);
        Assert.DoesNotContain(agents!, a => a.Id == loki.Agent.Id);
    }
}

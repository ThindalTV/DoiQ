using System.Net.Http.Json;
using System.Text.Json;
using DoiQ.Core.Outbox;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DoiQ.Tests;

/// <summary>One call a <see cref="WebhookReceiver"/> got.</summary>
public sealed record ReceivedCall(string Path, string Body, string? Timestamp, string? Signature, bool SignatureValid, int Status);

/// <summary>
/// Stands in for Hermes' webhook adapter: a real listener on a random local port that verifies the V2 signature
/// (hex HMAC-SHA256 over "&lt;timestamp&gt;.&lt;raw body&gt;") per route secret and records what it got.
/// </summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _lock = new();
    private readonly List<ReceivedCall> _calls = [];

    /// <summary>Status to answer with; change it to simulate Hermes being down (500) and recovering (200).</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>The secret the receiver verifies signatures with.</summary>
    public string Secret { get; }

    public string BaseUrl { get; private set; } = "";

    private WebhookReceiver(WebApplication app, string secret) { _app = app; Secret = secret; }

    public static async Task<WebhookReceiver> StartAsync(string secret)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var receiver = new WebhookReceiver(app, secret);
        app.MapPost("/{**path}", async (HttpContext ctx, string path) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var ts = ctx.Request.Headers["X-Webhook-Timestamp"].FirstOrDefault();
            var sig = ctx.Request.Headers["X-Webhook-Signature-V2"].FirstOrDefault();
            var valid = ts is not null && sig is not null && sig == OutboxProcessor.Sign(secret, ts, body);
            var status = receiver.StatusCode;
            lock (receiver._lock) receiver._calls.Add(new ReceivedCall("/" + path, body, ts, sig, valid, status));
            return Results.StatusCode(status);
        });
        await app.StartAsync();
        receiver.BaseUrl = app.Urls.First();
        return receiver;
    }

    public IReadOnlyList<ReceivedCall> Calls { get { lock (_lock) return _calls.ToList(); } }

    public IReadOnlyList<ReceivedCall> CallsTo(string path) => Calls.Where(c => c.Path == path).ToList();

    /// <summary>Waits until at least <paramref name="count"/> calls reached <paramref name="path"/>.</summary>
    public async Task<IReadOnlyList<ReceivedCall>> WaitForAsync(string path, int count = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var hit = CallsTo(path);
            if (hit.Count >= count) return hit;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Expected {count} call(s) to {path}, got {CallsTo(path).Count}.");
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// The real server (Program) over a throwaway SQLite file with a short debounce, polling and retry delays.
/// The database path travels through environment variables, so users of this class must be in the "Http" collection.
/// </summary>
public sealed class FlowHost : IAsyncDisposable
{
    public const string Password = "correct horse";
    private static readonly string[] EnvKeys = ["ConnectionStrings__Db", "Board__DebounceSeconds", "Outbox__PollSeconds", "Outbox__RetryBaseSeconds", "Outbox__RetryMaxSeconds", "Outbox__MaxAttempts"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private WebApplicationFactory<Program>? _factory;
    private readonly double _debounceSeconds;

    public HttpClient User { get; private set; } = null!;

    public FlowHost(double debounceSeconds = 1, string? dbPath = null)
    {
        _debounceSeconds = debounceSeconds;
        _dbPath = dbPath ?? Path.Combine(Path.GetTempPath(), $"doiq-flow-{Guid.NewGuid():N}.db");
    }

    public string DbPath => _dbPath;

    /// <summary>Boots the server; the first boot also runs first-run setup and signs the user in.</summary>
    public async Task StartAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("Board__DebounceSeconds", _debounceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("Outbox__PollSeconds", "0.2");
        Environment.SetEnvironmentVariable("Outbox__RetryBaseSeconds", "0.5");
        Environment.SetEnvironmentVariable("Outbox__RetryMaxSeconds", "1");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        User = _factory.CreateClient();
        var status = await User.GetFromJsonAsync<SetupStatusDto>("/api/setup/status");
        if (status!.NeedsSetup) (await User.PostAsJsonAsync("/api/setup", new SetupRequest(Password))).EnsureSuccessStatusCode();
        else (await User.PostAsJsonAsync("/api/auth/login", new LoginRequest(Password))).EnsureSuccessStatusCode();
    }

    /// <summary>Stops the server but keeps the database file (to simulate a restart).</summary>
    public async Task StopAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _factory = null;
    }

    public async Task<CreatedAgentResult> CreateAgentAsync(string name, bool triage = false, string? webhookUrl = null, string? secret = null, string? notifyUrl = null)
    {
        var resp = await User.PostAsJsonAsync("/api/agents", new CreateAgentRequest(name, triage, null, webhookUrl, secret, notifyUrl));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CreatedAgentResult>())!;
    }

    public async Task<TaskDetailDto> UserCreateAsync(string title, string? assignee = null, BoardStatus? status = null)
    {
        var resp = await User.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title, Assignee: assignee, Status: status));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TaskDetailDto>())!;
    }

    public async Task<TaskDetailDto> UserGetAsync(int id) => (await User.GetFromJsonAsync<TaskDetailDto>($"/api/tasks/{id}"))!;

    /// <summary>A plain HTTP client that authenticates as an agent with its API key.</summary>
    public HttpClient AgentHttp(string apiKey)
    {
        var http = _factory!.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return http;
    }

    public async Task<BoardDto> UserAddBoardAsync(string name)
    {
        var resp = await User.PostAsJsonAsync("/api/boards", new CreateBoardRequest(name));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<BoardDto>())!;
    }

    /// <summary>An MCP session at /mcp authenticated with an agent's API key.</summary>
    public async Task<McpClient> McpAsync(string apiKey)
    {
        var http = _factory!.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var k in EnvKeys) Environment.SetEnvironmentVariable(k, null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // pooled connections would keep the file locked
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch { } }
    }
}

public static class McpResultExtensions
{
    /// <summary>The text blocks of a tool result.</summary>
    public static string Text(this CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>Deserializes a successful tool result (fails the test with the tool's error text otherwise).</summary>
    public static T As<T>(this CallToolResult r)
    {
        Assert.False(r.IsError == true, "Tool failed: " + r.Text());
        return JsonSerializer.Deserialize<T>(r.Text(), FlowHost.Json)!;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DoiQ.Tests;

/// <summary>Request validation, login rate limiting, forwarded headers and idempotent retries, end to end over HTTP.</summary>
[Collection("Http")]   // the database path travels through an environment variable, so these classes must not overlap
public sealed class HardeningHttpTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public HardeningHttpTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            // The test server has no remote address; give every request the loopback one a local reverse proxy would have.
            b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, LoopbackClient>());
        });
    }

    private sealed class LoopbackClient : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware();
            });
            next(app);
        };
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpClient> SignedInUser(CancellationToken ct)
    {
        var user = _factory.CreateClient();
        (await user.PostAsJsonAsync("/api/setup", new SetupRequest(Password), ct)).EnsureSuccessStatusCode();
        return user;
    }

    [Theory]
    [InlineData("999")]
    [InlineData("2")]
    [InlineData("\"2\"")]
    [InlineData("\"Bogus\"")]
    public async Task PostTasks_StatusIsNotANamedLane_ReturnsBadRequest(string status)
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);

        // Act
        var response = await user.PostAsync("/api/tasks", Json($$"""{"title":"x","status":{{status}}}"""), cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await user.GetFromJsonAsync<List<TaskSummaryDto>>("/api/tasks", cts.Token) ?? []);
    }

    [Fact]
    public async Task PostTasks_StatusByName_CreatesTheTask()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);

        // Act
        var response = await user.PostAsync("/api/tasks", Json("""{"title":"x","status":"Waiting"}"""), cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostMove_StatusIsUndefined_ReturnsBadRequest()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);
        var created = await (await user.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Move me"), cts.Token)).Content.ReadFromJsonAsync<TaskDetailDto>(cts.Token);

        // Act
        var response = await user.PostAsync($"/api/tasks/{created!.Task.Id}/move", Json("""{"status":7}"""), cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("999")]
    [InlineData("Nope")]
    public async Task GetTasks_StatusQueryIsNotAName_ReturnsBadRequest(string status)
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);

        // Act
        var response = await user.GetAsync($"/api/tasks?status={status}", cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostLogin_MoreAttemptsThanTheLimit_ReturnsTooManyRequestsWithRetryAfter()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        (await SignedInUser(cts.Token)).Dispose();   // setup is the first attempt; the cookie client is not needed
        var anonymous = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        // Act
        for (var i = 0; i < 5; i++)
            statuses.Add((await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("wrong password"), cts.Token)).StatusCode);
        var limited = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("wrong password"), cts.Token);

        // Assert
        Assert.Contains(HttpStatusCode.Unauthorized, statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single()) > 0);
    }

    [Fact]
    public async Task PostSetup_MoreAttemptsThanTheLimit_ReturnsTooManyRequests()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var client = _factory.CreateClient();
        HttpResponseMessage last = null!;

        // Act
        for (var i = 0; i < 6; i++)
            last = await client.PostAsJsonAsync("/api/setup", new SetupRequest("short"), cts.Token);   // too short: never completes setup

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
    }

    [Fact]
    public async Task PostSetup_RequestForwardedAsHttps_SetsASecureCookie()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup") { Content = JsonContent.Create(new SetupRequest(Password)) };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        // Act
        var response = await client.SendAsync(request, cts.Token);

        // Assert
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSetup_PlainHttpRequest_SetsACookieWithoutSecure()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/api/setup", new SetupRequest(Password), cts.Token);

        // Assert
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth="));
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostTasks_ConcurrentCallsWithTheSameRequestKey_AllReturnTheSameTask()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => user.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Once", RequestKey: "same-key"), cts.Token)));

        // Assert
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"got {(int)r.StatusCode}"));
        var ids = new HashSet<int>();
        foreach (var r in responses) ids.Add((await r.Content.ReadFromJsonAsync<TaskDetailDto>(cts.Token))!.Task.Id);
        Assert.Single(ids);
        Assert.Single(await user.GetFromJsonAsync<List<TaskSummaryDto>>("/api/tasks", cts.Token) ?? []);
    }

    [Fact]
    public async Task PostComments_ConcurrentCallsWithTheSameRequestKey_AddOneComment()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);
        var task = (await (await user.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Talk"), cts.Token)).Content.ReadFromJsonAsync<TaskDetailDto>(cts.Token))!;

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => user.PostAsJsonAsync($"/api/tasks/{task.Task.Id}/comments", new AddCommentRequest("hello", "same-key"), cts.Token)));

        // Assert
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"got {(int)r.StatusCode}"));
        var detail = await user.GetFromJsonAsync<TaskDetailDto>($"/api/tasks/{task.Task.Id}", cts.Token);
        Assert.Single(detail!.Comments);
    }

    [Fact]
    public async Task PostSubtasks_ConcurrentCallsWithTheSameRequestKey_AddOneSubtask()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var user = await SignedInUser(cts.Token);
        var task = (await (await user.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Steps"), cts.Token)).Content.ReadFromJsonAsync<TaskDetailDto>(cts.Token))!;

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => user.PostAsJsonAsync($"/api/tasks/{task.Task.Id}/subtasks", new AddSubtaskRequest("step", RequestKey: "same-key"), cts.Token)));

        // Assert
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"got {(int)r.StatusCode}"));
        var detail = await user.GetFromJsonAsync<TaskDetailDto>($"/api/tasks/{task.Task.Id}", cts.Token);
        Assert.Single(detail!.Subtasks);
    }

    [Fact]
    public async Task Server_CookieIssuedByOneInstance_IsAcceptedByASecondInstanceOverTheSameDataFolder()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var folder = Path.Combine(Path.GetTempPath(), $"doiq-keys-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={Path.Combine(folder, "doiq.db")}");
        string cookie;
        using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production")))
        {
            var setup = await first.CreateClient().PostAsJsonAsync("/api/setup", new SetupRequest(Password), cts.Token);
            cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("doiq.auth=")).Split(';')[0];
        }
        using var second = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", cookie);

        // Act
        var response = await second.CreateClient().SendAsync(request, cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Directory.GetFiles(Path.Combine(folder, "keys")).Length > 0);
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*")) { try { File.Delete(f); } catch { /* best effort */ } }
    }
}

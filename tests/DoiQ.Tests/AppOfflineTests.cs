using System.Net;
using System.Net.Http.Json;
using DoiQ.App.Core;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DoiQ.Tests;

/// <summary>
/// The app's data path end to end: the shared <see cref="DoiQApiClient"/> over <see cref="OfflineCacheHandler"/>
/// against the real server. A board opened once stays readable after a restart with the server out of reach.
/// </summary>
[Collection("Http")]
public sealed class AppOfflineTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"doiq-test-{Guid.NewGuid():N}.db");
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"doiq-app-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));

    private CancellationToken Ct => _cts.Token;

    public AppOfflineTests()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
    }

    /// <summary>Stands in for the network: when <see cref="Down"/>, every request fails as an unreachable host does.</summary>
    private sealed class Network(HttpMessageHandler server) : DelegatingHandler(server)
    {
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Down ? throw new HttpRequestException("No route to host.") : base.SendAsync(request, ct);
    }

    /// <summary>What SocketsHttpHandler does with its CookieContainer; the test server's handler keeps no cookies.</summary>
    private sealed class Cookies(CookieContainer jar, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = jar.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var c in set) jar.SetCookies(request.RequestUri!, c);
            return response;
        }
    }

    /// <summary>One app start: settings, cookies and cache read from the data folder, like the MAUI app's DI does.</summary>
    private (DoiQApiClient Api, ConnectivityState Connectivity, Network Network) StartApp()
    {
        var settings = new ServerSettings(_dataDir);
        if (settings.BaseUrl is null) settings.Set(_factory.Server.BaseAddress.ToString());
        var jar = new PersistentCookieJar(_dataDir);
        jar.Load();
        var connectivity = new ConnectivityState();
        var network = new Network(_factory.Server.CreateHandler());
        var handler = new OfflineCacheHandler(settings, connectivity, Path.Combine(_dataDir, "cache"), jar)
        {
            InnerHandler = new Cookies(jar.Container, network),
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri(ServiceCollectionExtensions.PlaceholderBase) };
        return (new DoiQApiClient(http), connectivity, network);
    }

    [Fact]
    public async Task Board_ReopenedWhileServerUnreachable_ShowsTasksAsLastSeen()
    {
        var seed = _factory.CreateClient();
        (await seed.PostAsJsonAsync("/api/setup", new SetupRequest(Password), Ct)).EnsureSuccessStatusCode();
        foreach (var title in new[] { "Buy milk", "Fix the bike" })
            (await seed.PostAsJsonAsync("/api/tasks", new CreateTaskRequest(title), Ct)).EnsureSuccessStatusCode();

        var first = StartApp();
        await first.Api.LoginAsync(Password);
        var online = (await first.Api.GetBoard(Ct)).Select(t => t.Title).Order().ToList();
        Assert.Equal(["Buy milk", "Fix the bike"], online);

        // A change after the board was last opened: offline, the app shows the board as it was opened.
        (await seed.PostAsJsonAsync("/api/tasks", new CreateTaskRequest("Added later"), Ct)).EnsureSuccessStatusCode();

        var second = StartApp();
        second.Network.Down = true;
        Assert.True(await second.Api.IsSignedInAsync());
        var offline = (await second.Api.GetBoard(Ct)).Select(t => t.Title).Order().ToList();

        Assert.Equal(online, offline);
        Assert.True(second.Connectivity.IsOffline);
        Assert.NotNull(second.Connectivity.ShowingCachedSince);
    }

    [Fact]
    public async Task Write_WhileServerUnreachable_Fails()
    {
        var seed = _factory.CreateClient();
        (await seed.PostAsJsonAsync("/api/setup", new SetupRequest(Password), Ct)).EnsureSuccessStatusCode();

        var app = StartApp();
        await app.Api.LoginAsync(Password);
        await app.Api.GetBoard(Ct);
        app.Network.Down = true;

        await Assert.ThrowsAnyAsync<Exception>(() => app.Api.CreateTaskAsync("Offline task"));
    }

    public void Dispose()
    {
        _cts.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Db", null);
        try { File.Delete(_dbPath); } catch (IOException) { /* still held by the closing server */ }
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

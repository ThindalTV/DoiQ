using System.Net;
using System.Text;
using DoiQ.App.Core;

namespace DoiQ.App.Tests;

public sealed class OfflineCacheHandlerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "doiq-app-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(1));
    private readonly FakeInner _inner = new();
    private readonly ServerSettings _settings;
    private readonly ConnectivityState _state = new();
    private readonly PersistentCookieJar _jar;
    private readonly OfflineCacheOptions _options = new() { NetworkTimeout = TimeSpan.FromSeconds(5), PruneEvery = 1 };
    private readonly OfflineCacheHandler _handler;
    private readonly HttpClient _http;

    private CancellationToken Ct => _cts.Token;

    public OfflineCacheHandlerTests()
    {
        _settings = new ServerSettings(_dir);
        _settings.Set("http://srv1:8080");
        _jar = new PersistentCookieJar(_dir);
        _handler = new OfflineCacheHandler(_settings, _state, Path.Combine(_dir, "cache"), _jar, _options) { InnerHandler = _inner };
        _http = new HttpClient(_handler) { BaseAddress = new Uri(ServiceCollectionExtensions.PlaceholderBase) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _cts.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Send_GetSucceedsThenServerDown_ServesCachedWithHeader()
    {
        _inner.Respond("[1,2]");
        await _http.GetAsync("api/boards", Ct);

        _inner.Fail = true;
        var response = await _http.GetAsync("api/boards", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[1,2]", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(DateTimeOffset.TryParse(response.Headers.GetValues(OfflineCacheHandler.CachedHeader).Single(), out _));
    }

    [Fact]
    public async Task Send_GetWithQuery_CachesPerQuery()
    {
        _inner.Respond("a");
        await _http.GetAsync("api/inbox?x=1", Ct);
        _inner.Fail = true;

        Assert.Equal("a", await (await _http.GetAsync("api/inbox?x=1", Ct)).Content.ReadAsStringAsync(Ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/inbox?x=2", Ct));
    }

    [Fact]
    public async Task Send_FreshResponseWhileOnline_HasNoCachedHeaderAndBodyStillReadable()
    {
        _inner.Respond("fresh");
        var response = await _http.GetAsync("api/boards", Ct);

        Assert.False(response.Headers.Contains(OfflineCacheHandler.CachedHeader));
        Assert.Equal("fresh", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Send_PostSucceedsThenServerDown_IsNotCachedAndThrows()
    {
        _inner.Respond("x");
        await _http.PostAsync("api/boards/1/tasks", new StringContent("{}"), Ct);
        _inner.Fail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.PostAsync("api/boards/1/tasks", new StringContent("{}"), Ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/boards/1/tasks", Ct));
    }

    [Fact]
    public async Task Send_EventsGet_IsNeverCached()
    {
        _inner.Respond("[]");
        await _http.GetAsync("api/events?after=3", Ct);
        await _http.GetAsync("api/events/latest", Ct);
        _inner.Fail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/events?after=3", Ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/events/latest", Ct));
    }

    [Fact]
    public async Task Send_GetOutsideApi_IsNotCached()
    {
        _inner.Respond("<html/>");
        await _http.GetAsync("index.html", Ct);
        _inner.Fail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("index.html", Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Send_ErrorStatusWithCachedEntry_PassesStatusThroughAndKeepsCache(HttpStatusCode status)
    {
        _inner.Respond("good");
        await _http.GetAsync("api/me", Ct);

        _inner.Respond("bad", status);
        var response = await _http.GetAsync("api/me", Ct);
        Assert.Equal(status, response.StatusCode);
        Assert.False(response.Headers.Contains(OfflineCacheHandler.CachedHeader));
        Assert.False(_state.IsOffline);

        _inner.Fail = true;   // the 401 must not have wiped the cache
        Assert.Equal("good", await (await _http.GetAsync("api/me", Ct)).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Send_CallerCancels_RethrowsInsteadOfServingCache()
    {
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);
        _inner.Hang = true;

        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _http.GetAsync("api/boards", caller.Token));
        Assert.False(_state.IsOffline);
    }

    [Fact]
    public async Task Send_ServerTimesOut_ServesCache()
    {
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);
        _options.NetworkTimeout = TimeSpan.FromMilliseconds(50);
        _inner.Hang = true;

        var response = await _http.GetAsync("api/boards", Ct);

        Assert.True(response.Headers.Contains(OfflineCacheHandler.CachedHeader));
        Assert.True(_state.IsOffline);
    }

    [Fact]
    public async Task Send_FailureWithNoCacheEntry_Rethrows()
    {
        _inner.Fail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/boards", Ct));
        Assert.True(_state.IsOffline);
        Assert.Null(_state.ShowingCachedSince);
    }

    [Fact]
    public async Task Send_LogoutSucceeds_ClearsCacheAndCookies()
    {
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);
        _jar.Container.Add(new Uri("http://srv1:8080/"), new Cookie("doiq.auth", "t"));
        _jar.Save();

        await _http.PostAsync("api/auth/logout", null, Ct);
        _inner.Fail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/boards", Ct));
        Assert.Empty(_jar.Container.GetAllCookies());
    }

    [Fact]
    public async Task Send_LogoutFails_KeepsCache()
    {
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);

        _inner.Respond("", HttpStatusCode.InternalServerError);
        await _http.PostAsync("api/auth/logout", null, Ct);
        _inner.Fail = true;

        Assert.Equal("x", await (await _http.GetAsync("api/boards", Ct)).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ClearCache_AfterCaching_RemovesEntries()
    {
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);

        _handler.ClearCache();
        _inner.Fail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/boards", Ct));
    }

    [Fact]
    public async Task Send_RelativeUri_IsRewrittenToConfiguredServer()
    {
        _settings.Set("https://doiq.example.com/sub");
        _inner.Respond("x");

        await _http.GetAsync("api/boards/5/tasks?a=b", Ct);

        Assert.Equal("https://doiq.example.com/sub/api/boards/5/tasks?a=b", _inner.LastUri?.ToString());
    }

    [Fact]
    public async Task Send_NoServerConfigured_ThrowsHttpRequestException()
    {
        _settings.Clear();

        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/boards", Ct));
    }

    [Fact]
    public async Task Send_SwitchingServer_DoesNotServeOtherServersCache()
    {
        _inner.Respond("one");
        await _http.GetAsync("api/boards", Ct);

        _settings.Set("http://srv2/");
        _inner.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/boards", Ct));

        _settings.Set("http://srv1:8080/");
        Assert.Equal("one", await (await _http.GetAsync("api/boards", Ct)).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Send_OfflineThenOnline_FlipsConnectivityAndRaisesChanged()
    {
        var changes = 0;
        _state.Changed += () => changes++;
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);
        Assert.False(_state.IsOffline);
        Assert.Equal(0, changes);

        _inner.Fail = true;
        var response = await _http.GetAsync("api/boards", Ct);
        var fetched = DateTimeOffset.Parse(response.Headers.GetValues(OfflineCacheHandler.CachedHeader).Single());
        Assert.True(_state.IsOffline);
        Assert.Equal(fetched, _state.ShowingCachedSince);
        Assert.Equal(1, changes);

        _inner.Fail = false;
        await _http.GetAsync("api/boards", Ct);
        Assert.False(_state.IsOffline);
        Assert.Null(_state.ShowingCachedSince);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Send_ErrorStatusWhileOffline_FlipsBackOnline()
    {
        _inner.Respond("x");
        await _http.GetAsync("api/boards", Ct);
        _inner.Fail = true;
        await _http.GetAsync("api/boards", Ct);

        _inner.Fail = false;
        _inner.Respond("", HttpStatusCode.Unauthorized);
        await _http.GetAsync("api/me", Ct);

        Assert.False(_state.IsOffline);
    }

    [Fact]
    public async Task Send_SetCookieResponse_PersistsCookiesForNextRun()
    {
        var handler = new OfflineCacheHandler(_settings, _state, Path.Combine(_dir, "cache2"), _jar, _options)
        {
            InnerHandler = new CookieSettingInner(_jar),
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(ServiceCollectionExtensions.PlaceholderBase) };

        await http.PostAsync("api/auth/login", new StringContent("{}"), Ct);

        var reloaded = new PersistentCookieJar(_dir);
        reloaded.Load();
        Assert.Contains(reloaded.Container.GetAllCookies(), c => c.Name == "doiq.auth");
    }

    [Fact]
    public async Task Send_MoreEntriesThanCap_PrunesOldest()
    {
        _options.MaxEntries = 3;
        _inner.Respond("x");
        for (var i = 0; i < 6; i++)
        {
            await _http.GetAsync($"api/tasks/{i}", Ct);
            await Task.Delay(30, Ct);   // distinct write times
        }

        _inner.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("api/tasks/0", Ct));
        Assert.Equal("x", await (await _http.GetAsync("api/tasks/5", Ct)).Content.ReadAsStringAsync(Ct));
    }

    private sealed class FakeInner : HttpMessageHandler
    {
        private string _body = "";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public bool Fail { get; set; }
        public bool Hang { get; set; }
        public Uri? LastUri { get; private set; }

        public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            if (Fail) throw new HttpRequestException("down");
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CookieSettingInner(PersistentCookieJar jar) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            jar.Container.Add(request.RequestUri!, new Cookie("doiq.auth", "tok") { Expires = DateTime.UtcNow.AddDays(30) });
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "doiq.auth=tok");
            return Task.FromResult(response);
        }
    }
}

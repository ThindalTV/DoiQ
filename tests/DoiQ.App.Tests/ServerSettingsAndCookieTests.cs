using System.Net;
using DoiQ.App.Core;

namespace DoiQ.App.Tests;

public sealed class ServerSettingsAndCookieTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "doiq-app-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("doiq.lan:8080", "http://doiq.lan:8080/")]
    [InlineData("  https://doiq.example.com  ", "https://doiq.example.com/")]
    [InlineData("http://host/sub", "http://host/sub/")]
    [InlineData("http://host/sub/?a=1#frag", "http://host/sub/")]
    [InlineData("localhost:5000", "http://localhost:5000/")]
    public void TryNormalise_ValidInput_ReturnsHttpUrlWithTrailingSlash(string input, string expected)
    {
        Assert.True(ServerSettings.TryNormalise(input, out var url));
        Assert.Equal(expected, url.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host/")]
    [InlineData("http://")]
    public void TryNormalise_InvalidInput_ReturnsFalse(string? input)
    {
        Assert.False(ServerSettings.TryNormalise(input, out _));
    }

    [Fact]
    public void Set_ValidAddress_PersistsAndRaisesChangedOnce()
    {
        var settings = new ServerSettings(_dir);
        var changes = 0;
        settings.Changed += () => changes++;

        Assert.True(settings.Set("doiq.lan:8080"));
        Assert.True(settings.Set("http://doiq.lan:8080/"));

        Assert.Equal(1, changes);
        Assert.Equal("http://doiq.lan:8080/", new ServerSettings(_dir).BaseUrl?.AbsoluteUri);
    }

    [Fact]
    public void Set_InvalidAddress_KeepsPreviousValue()
    {
        var settings = new ServerSettings(_dir);
        settings.Set("host1");

        Assert.False(settings.Set("ftp://nope"));

        Assert.Equal("http://host1/", settings.BaseUrl?.AbsoluteUri);
    }

    [Fact]
    public void Clear_AfterSet_RemovesPersistedValue()
    {
        var settings = new ServerSettings(_dir);
        settings.Set("host1");

        settings.Clear();

        Assert.Null(new ServerSettings(_dir).BaseUrl);
    }

    [Fact]
    public void Constructor_DamagedFile_StartsUnconfigured()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "server.json"), "{not json");

        Assert.Null(new ServerSettings(_dir).BaseUrl);
    }

    [Fact]
    public void Resolve_WithServer_AppendsRelativePath()
    {
        var settings = new ServerSettings(_dir);
        settings.Set("http://host/sub");

        Assert.Equal("http://host/sub/hubs/board", settings.Resolve("/hubs/board")?.AbsoluteUri);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsCookies()
    {
        var jar = new PersistentCookieJar(_dir);
        var expires = DateTime.UtcNow.AddDays(30);
        jar.Container.Add(new Uri("http://doiq.lan:8080/"),
            new Cookie("doiq.auth", "secret") { Expires = expires, HttpOnly = true, Path = "/" });
        jar.Save();

        var reloaded = new PersistentCookieJar(_dir);
        reloaded.Load();

        var cookie = Assert.Single(reloaded.Container.GetAllCookies());
        Assert.Equal("doiq.auth", cookie.Name);
        Assert.Equal("secret", cookie.Value);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/", cookie.Path);
        Assert.True(Math.Abs((cookie.Expires.ToUniversalTime() - expires).TotalSeconds) < 2);
        Assert.Equal("doiq.auth=secret", reloaded.Container.GetCookieHeader(new Uri("http://doiq.lan:8080/api/me")));
    }

    [Fact]
    public void Load_ExpiredCookie_IsSkipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "cookies.json"),
            """[{"Name":"old","Value":"v","Domain":"h","Path":"/","ExpiresUtc":"2001-01-01T00:00:00Z","Secure":false,"HttpOnly":false}]""");
        var jar = new PersistentCookieJar(_dir);

        jar.Load();

        Assert.Empty(jar.Container.GetAllCookies());
    }

    [Fact]
    public void Clear_AfterSave_RemovesCookiesInMemoryAndOnDisk()
    {
        var jar = new PersistentCookieJar(_dir);
        jar.Container.Add(new Uri("http://h/"), new Cookie("a", "b"));
        jar.Save();

        jar.Clear();

        Assert.Empty(jar.Container.GetAllCookies());
        var reloaded = new PersistentCookieJar(_dir);
        reloaded.Load();
        Assert.Empty(reloaded.Container.GetAllCookies());
    }
}

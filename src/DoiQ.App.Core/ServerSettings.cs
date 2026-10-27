using System.Text.Json;

namespace DoiQ.App.Core;

/// <summary>
/// The server the app talks to, persisted as a small JSON file. <see cref="BaseUrl"/> always ends in a slash so
/// relative API paths can be appended to it.
/// </summary>
public sealed class ServerSettings
{
    private readonly string _file;
    private readonly object _gate = new();
    private Uri? _baseUrl;

    public ServerSettings(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _file = Path.Combine(dataDirectory, "server.json");
        _baseUrl = Read();
    }

    public event Action? Changed;

    public Uri? BaseUrl
    {
        get { lock (_gate) return _baseUrl; }
    }

    /// <summary>Normalises and stores the address; false (and no change) when it is not a usable http(s) address.</summary>
    public bool Set(string? input)
    {
        if (!TryNormalise(input, out var url)) return false;
        Apply(url);
        return true;
    }

    public void Clear() => Apply(null);

    /// <summary>The absolute address of <paramref name="relative"/> on the configured server (e.g. the hub path), or null when none is configured.</summary>
    public Uri? Resolve(string relative)
    {
        var baseUrl = BaseUrl;
        return baseUrl is null ? null : new Uri(baseUrl, relative.TrimStart('/'));
    }

    /// <summary>Accepts "doiq.lan:8080" (assumes http://), drops query and fragment, and ensures a trailing slash.</summary>
    public static bool TryNormalise(string? input, out Uri url)
    {
        url = null!;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;

        var builder = new UriBuilder(parsed) { Query = "", Fragment = "", UserName = "", Password = "" };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        url = builder.Uri;
        return true;
    }

    private void Apply(Uri? url)
    {
        lock (_gate)
        {
            if (_baseUrl == url) return;
            _baseUrl = url;
            Write(url);
        }
        Changed?.Invoke();
    }

    private Uri? Read()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file));
            return TryNormalise(data?.BaseUrl, out var url) ? url : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;   // a damaged settings file just means "not configured yet"
        }
    }

    private void Write(Uri? url)
    {
        var temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(url?.AbsoluteUri)));
        File.Move(temp, _file, overwrite: true);
    }

    private sealed record Stored(string? BaseUrl);
}

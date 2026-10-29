using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DoiQ.App.Core;

/// <summary>
/// Points requests at the configured server and keeps the last good answer to each GET under <c>api/</c>, so the
/// app can show it read-only when the server cannot be reached. Only network failures fall back to the cache;
/// HTTP error statuses pass through untouched.
/// </summary>
public sealed class OfflineCacheHandler : DelegatingHandler
{
    public const string CachedHeader = "X-DoiQ-Cached";

    private readonly ServerSettings _settings;
    private readonly ConnectivityState _connectivity;
    private readonly string _cacheRoot;
    private readonly PersistentCookieJar? _jar;
    private readonly OfflineCacheOptions _options;
    private readonly object _gate = new();
    private int _storesSincePrune;
    /// <summary>Bumped by every clear; a GET that started before one must not write its answer back afterwards.</summary>
    private long _generation;

    public OfflineCacheHandler(ServerSettings settings, ConnectivityState connectivity, string cacheRoot,
        PersistentCookieJar? jar = null, OfflineCacheOptions? options = null)
    {
        _settings = settings;
        _connectivity = connectivity;
        _cacheRoot = cacheRoot;
        _jar = jar;
        _options = options ?? new OfflineCacheOptions();
    }

    /// <summary>Drops everything cached for the configured server.</summary>
    public void ClearCache()
    {
        if (_settings.BaseUrl is { } server) ClearDirectory(CacheDirectory(server));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var server = _settings.BaseUrl ?? throw new HttpRequestException("No server is configured.");
        var relative = (request.RequestUri ?? throw new InvalidOperationException("Request has no URI."))
            .PathAndQuery.TrimStart('/');
        request.RequestUri = new Uri(server, relative);

        var cacheable = request.Method == HttpMethod.Get && IsCacheable(relative);
        var key = server.AbsoluteUri + relative;
        var generation = Interlocked.Read(ref _generation);

        HttpResponseMessage response;
        try
        {
            response = await SendWithTimeout(request, ct);
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            if (cacheable && await TryRead(server, key, ct) is { } entry)
            {
                _connectivity.MarkOffline(entry.FetchedAtUtc);
                return Rebuild(request, entry);
            }
            _connectivity.MarkOffline(null);
            throw;
        }

        _connectivity.MarkOnline();
        if (response.Headers.Contains("Set-Cookie")) _jar?.Save();

        if (response.IsSuccessStatusCode)
        {
            if (cacheable)
            {
                // Buffer so the caller still gets a readable body after we have copied it.
                await response.Content.LoadIntoBufferAsync(ct);
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                await Store(server, key, new CacheEntry(key, (int)response.StatusCode,
                    response.Content.Headers.ContentType?.ToString(), body, DateTimeOffset.UtcNow), generation, ct);
            }
            else if (request.Method == HttpMethod.Post && IsPath(relative, "api/auth/logout"))
            {
                ClearDirectory(CacheDirectory(server));
                _jar?.Clear();
            }
            else if (request.Method == HttpMethod.Post && IsPath(relative, "api/auth/login"))
            {
                ClearDirectory(CacheDirectory(server));   // a different user must not see the previous one's data
            }
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendWithTimeout(HttpRequestMessage request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.NetworkTimeout);
        // The timeout only covers waiting for the response headers; the body is read under the caller's token.
        return await base.SendAsync(request, cts.Token);
    }

    private static bool IsNetworkFailure(Exception e, CancellationToken ct) => e switch
    {
        HttpRequestException => true,
        SocketException => true,
        OperationCanceledException => !ct.IsCancellationRequested,   // our timeout, not the caller giving up
        _ => false,
    };

    private static bool IsCacheable(string relative) =>
        relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
        && !relative.StartsWith("api/events", StringComparison.OrdinalIgnoreCase);   // catch-up must never be stale

    private static bool IsPath(string relative, string path)
    {
        var q = relative.IndexOf('?');
        return string.Equals(q < 0 ? relative : relative[..q], path, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Rebuild(HttpRequestMessage request, CacheEntry entry)
    {
        var response = new HttpResponseMessage((HttpStatusCode)entry.Status)
        {
            Content = new ByteArrayContent(entry.Body),
            RequestMessage = request,
        };
        if (entry.ContentType is { } type && MediaTypeHeaderValue.TryParse(type, out var parsed))
            response.Content.Headers.ContentType = parsed;
        response.Headers.TryAddWithoutValidation(CachedHeader, entry.FetchedAtUtc.ToString("O"));
        return response;
    }

    // ---- disk ----

    private string CacheDirectory(Uri server) => Path.Combine(_cacheRoot, Hash(server.AbsoluteUri.ToLowerInvariant())[..16]);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string EntryPath(string directory, string key) => Path.Combine(directory, Hash(key) + ".json");

    private async Task<CacheEntry?> TryRead(Uri server, string key, CancellationToken ct)
    {
        var path = EntryPath(CacheDirectory(server), key);
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllBytesAsync(path, ct));
            return entry is not null && entry.Key == key ? entry : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes beside the server directories, then moves the file in under the lock only if no clear happened since
    /// the request started (<paramref name="generation"/>): a GET still downloading when the user signs out or a
    /// different user signs in must not put the old session's data back on disk.
    /// </summary>
    private async Task Store(Uri server, string key, CacheEntry entry, long generation, CancellationToken ct)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            temp = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(entry), ct);
            var directory = CacheDirectory(server);
            lock (_gate)
            {
                if (Interlocked.Read(ref _generation) != generation) return;
                Directory.CreateDirectory(directory);
                File.Move(temp, EntryPath(directory, key), overwrite: true);   // readers see the old or the new file, never half of one
                temp = null;
            }
            MaybePrune(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Caching is best effort; a full disk must not fail the request.
        }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private void MaybePrune(string directory)
    {
        lock (_gate)
        {
            if (++_storesSincePrune < _options.PruneEvery) return;
            _storesSincePrune = 0;
            var files = new DirectoryInfo(directory).GetFiles("*.json");
            foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(_options.MaxEntries))
            {
                try { old.Delete(); } catch (IOException) { }
            }
        }
    }

    private void ClearDirectory(string directory)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record CacheEntry(string Key, int Status, string? ContentType, byte[] Body, DateTimeOffset FetchedAtUtc);
}

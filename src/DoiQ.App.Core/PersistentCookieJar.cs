using System.Net;
using System.Text.Json;

namespace DoiQ.App.Core;

/// <summary>
/// A <see cref="CookieContainer"/> that survives app restarts, so the 30-day auth cookie keeps the user signed in.
/// Hand <see cref="Container"/> to the HTTP handler (and to SignalR's cookie container).
/// </summary>
public sealed class PersistentCookieJar
{
    private readonly string _file;
    private readonly object _gate = new();

    public PersistentCookieJar(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _file = Path.Combine(dataDirectory, "cookies.json");
    }

    public CookieContainer Container { get; } = new();

    /// <summary>Restores cookies saved earlier; a missing or damaged file is ignored.</summary>
    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_file)) return;
                var stored = JsonSerializer.Deserialize<List<StoredCookie>>(File.ReadAllText(_file)) ?? [];
                foreach (var c in stored)
                {
                    if (c.ExpiresUtc is { } exp && exp <= DateTime.UtcNow) continue;
                    var cookie = new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly };
                    if (c.ExpiresUtc is { } e) cookie.Expires = e;
                    Container.Add(cookie);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or CookieException)
            {
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var stored = Container.GetAllCookies()
                .Where(c => !c.Expired && (c.Expires == DateTime.MinValue || c.Expires.ToUniversalTime() > now))
                .Select(c => new StoredCookie(c.Name, c.Value, c.Domain, c.Path,
                    c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime(), c.Secure, c.HttpOnly))
                .ToList();
            var temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored));
            File.Move(temp, _file, overwrite: true);
        }
    }

    /// <summary>Forgets every cookie, in memory and on disk (logout).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            // Adding an expired twin is how a CookieContainer drops a cookie; flagging the existing one is not enough.
            foreach (Cookie c in Container.GetAllCookies())
                Container.Add(new Cookie(c.Name, "", c.Path, c.Domain) { Expires = DateTime.UtcNow.AddDays(-1) });
            if (File.Exists(_file)) File.Delete(_file);
        }
    }

    private sealed record StoredCookie(string Name, string Value, string Domain, string Path, DateTime? ExpiresUtc, bool Secure, bool HttpOnly);
}

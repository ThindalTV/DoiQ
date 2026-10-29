namespace DoiQ.App.Core;

/// <summary>
/// Whether the last request reached the server. While offline, the UI is showing cached data from
/// <see cref="ShowingCachedSince"/> (read-only).
/// </summary>
public sealed class ConnectivityState
{
    private readonly object _gate = new();
    private bool _offline;
    private DateTimeOffset? _since;

    public event Action? Changed;

    public bool IsOffline { get { lock (_gate) return _offline; } }

    /// <summary>Fetch time of the most recently served cached response; null when online.</summary>
    public DateTimeOffset? ShowingCachedSince { get { lock (_gate) return _since; } }

    /// <summary>Any answer from the server, even an error status, means it is reachable.</summary>
    public void MarkOnline() => Update(false, null, keepSince: false);

    /// <summary>The server could not be reached; <paramref name="cachedFetchedAt"/> is set when a cached response was served instead.</summary>
    public void MarkOffline(DateTimeOffset? cachedFetchedAt) => Update(true, cachedFetchedAt, keepSince: cachedFetchedAt is null);

    private void Update(bool offline, DateTimeOffset? since, bool keepSince)
    {
        lock (_gate)
        {
            var newSince = keepSince && offline ? _since : since;
            if (_offline == offline && _since == newSince) return;
            _offline = offline;
            _since = newSince;
        }
        Changed?.Invoke();
    }
}

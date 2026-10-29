namespace DoiQ.Client.Services;

/// <summary>
/// Set by a host that keeps a cache of what it read (the MAUI app): whether the server is out of reach and the board
/// shows what was cached. The browser client registers none.
/// </summary>
public interface IOfflineStatus
{
    bool IsOffline { get; }

    /// <summary>When the newest cached answer shown while offline was fetched.</summary>
    DateTimeOffset? CachedAt { get; }

    event Action? Changed;
}

using DoiQ.App.Core;
using DoiQ.Client.Services;

namespace DoiQ.App.Services;

/// <summary>The cache handler's <see cref="ConnectivityState"/>, as the shared UI's offline banner reads it.</summary>
public sealed class OfflineStatus : IOfflineStatus
{
    private readonly ConnectivityState _state;

    public OfflineStatus(ConnectivityState state)
    {
        _state = state;
        _state.Changed += () => Changed?.Invoke();
    }

    public bool IsOffline => _state.IsOffline;
    public DateTimeOffset? CachedAt => _state.ShowingCachedSince;
    public event Action? Changed;
}

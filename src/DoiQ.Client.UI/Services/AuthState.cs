namespace DoiQ.Client.Services;

public enum AuthStatus { Unknown, NeedsSetup, SignedOut, SignedIn }

/// <summary>Where the user stands with the server: first run, signed out, or signed in. The shell routes on it.</summary>
public sealed class AuthState
{
    private readonly IDoiQApiClient _api;

    public AuthState(IDoiQApiClient api)
    {
        _api = api;
        _api.Unauthorized += () => Set(AuthStatus.SignedOut);
    }

    public AuthStatus Status { get; private set; } = AuthStatus.Unknown;
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        try
        {
            if ((await _api.GetSetupStatusAsync()).NeedsSetup) { Set(AuthStatus.NeedsSetup); return; }
            Set(await _api.IsSignedInAsync() ? AuthStatus.SignedIn : AuthStatus.SignedOut);
        }
        catch (Exception)
        {
            // Server unreachable or the route is missing: fall back to the login page rather than a blank screen.
            Set(AuthStatus.SignedOut);
        }
    }

    public void Set(AuthStatus s)
    {
        if (Status == s) return;
        Status = s;
        Changed?.Invoke();
    }
}

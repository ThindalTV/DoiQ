namespace DoiQ.Server.Auth;

/// <summary>Brake on password guessing. Bound from "Auth:RateLimit"; the defaults need no configuration.</summary>
public sealed class LoginLimitOptions
{
    public const string Section = "Auth:RateLimit";
    public const string PolicyName = "auth";

    /// <summary>Login and first-run setup attempts allowed per client IP in one window.</summary>
    public int PermitLimit { get; set; } = 5;

    /// <summary>Length of the window in seconds; also the Retry-After a rejected caller is told.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Password verifications (PBKDF2, deliberately slow) that may run at the same time; the rest wait.</summary>
    public int MaxConcurrentVerifications { get; set; } = 2;
}

/// <summary>
/// Reverse proxies whose X-Forwarded-For / X-Forwarded-Proto are believed. Bound from "Proxy". Empty by default:
/// then only a proxy on the same machine (loopback) is trusted. Without a trusted proxy the headers are ignored.
/// </summary>
public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Single proxy addresses, e.g. "10.0.0.5".</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Networks in CIDR notation, e.g. "172.16.0.0/12" for the default Docker bridge range.</summary>
    public string[] KnownNetworks { get; set; } = [];
}

/// <summary>Caps how many PBKDF2 checks run at once so a burst of logins cannot occupy every core.</summary>
public sealed class PasswordVerifyGate(Microsoft.Extensions.Options.IOptions<LoginLimitOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxConcurrentVerifications));

    public async Task<T> Run<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try { return await work(); }
        finally { _slots.Release(); }
    }

    public void Dispose() => _slots.Dispose();
}

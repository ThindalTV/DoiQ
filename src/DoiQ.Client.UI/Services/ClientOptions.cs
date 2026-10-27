using Microsoft.AspNetCore.Http.Connections.Client;

namespace DoiQ.Client.Services;

/// <summary>
/// What differs between the hosts of this UI. The browser client registers none and gets the defaults; the MAUI app
/// talks to a server it is not served from and leaves administration to the web UI.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>The browser client's options, for components when the host registered none.</summary>
    public static readonly ClientOptions Default = new();

    /// <summary>Board and agent administration, the agent connection facts and the password change.</summary>
    public bool Administration { get; init; } = true;

    /// <summary>Where the hub is; null means next to the page, as when the server serves this client.</summary>
    public Func<Uri>? HubUrl { get; init; }

    /// <summary>Extra setup for the hub's HTTP connection (the app hands it the cookies it signed in with).</summary>
    public Action<HttpConnectionOptions>? ConfigureHub { get; init; }

    /// <summary>A component the host adds to Settings and the sign-in page (the app's server address).</summary>
    public Type? HostSection { get; init; }
}
